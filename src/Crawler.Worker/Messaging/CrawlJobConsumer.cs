using System.Text.Json;
using Crawler.Domain.Jobs;
using Crawler.Domain.Messages;
using Crawler.Infrastructure.Messaging;
using Crawler.Worker.Crawling;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Crawler.Worker.Messaging;

/// <summary>
/// Consumes CrawlJobRequested from crawl.jobs. Manual acks: a message is only removed from the queue
/// after the job is fully processed, so a worker crash means RabbitMQ redelivers it (at-least-once).
/// </summary>
public sealed class CrawlJobConsumer(
    RabbitMqConnectionProvider connections,
    RabbitMqPublisher publisher,
    IJobRepository jobs,
    IOptions<MessageRetryOptions> retryOptions,
    IServiceScopeFactory scopes,
    ILogger<CrawlJobConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Loop so that if the broker closes just our channel (e.g. consumer ack timeout), we start consuming again.
        // A dropped *connection* is different: the client's automatic recovery restores the channel and consumer itself.
        while (!stoppingToken.IsCancellationRequested)
        {
            var channel = await StartConsumingAsync(stoppingToken);

            // Check every 5s. Seen closed twice in a row while the connection is up = closed by the broker for good.
            var closedChecks = 0;
            while (closedChecks < 2)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException)
                {
                    await channel.CloseAsync();
                    return;
                }

                closedChecks = channel.IsClosed && connections.IsOpen ? closedChecks + 1 : 0;
            }

            logger.LogWarning("Channel was closed by the broker; reopening consumer");
            await channel.DisposeAsync();
        }
    }

    private async Task<IChannel> StartConsumingAsync(CancellationToken stoppingToken)
    {
        var connection = await connections.GetConnectionAsync(stoppingToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        // One job at a time per worker; scale out by running more worker containers.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(RabbitMqTopology.JobsQueue, autoAck: false, consumer, stoppingToken);

        logger.LogInformation("Consuming {Queue}", RabbitMqTopology.JobsQueue);
        return channel;
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        var message = TryDeserialize(delivery.Body.Span);
        if (message is null)
        {
            // Poison: can never be processed, so retrying is pointless → dead-letter queue.
            logger.LogError("Unreadable message {MessageId}; sending to dead-letter queue", delivery.BasicProperties.MessageId);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false);
            return;
        }

        using var _ = logger.BeginScope("JobId:{JobId} CorrelationId:{CorrelationId} MessageId:{MessageId}",
            message.JobId, message.CorrelationId, message.MessageId);

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<CrawlJobProcessor>().ProcessAsync(message.JobId, stoppingToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Worker shutting down mid-crawl: put it back; the next worker resumes from the pages still Pending.
            // (If the channel is already closing, RabbitMQ requeues the unacked message anyway.)
            logger.LogInformation("Shutdown during crawl; requeueing");
            try { await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true); }
            catch (Exception ex) when (ex is RabbitMQ.Client.Exceptions.AlreadyClosedException or OperationCanceledException) { }
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(channel, delivery, message, ex, stoppingToken);
        }
    }

    /// <summary>
    /// Transient failure → copy to the retry queue (waits 10s, then returns to crawl.jobs) with attempt+1.
    /// Permanent failure, or out of attempts → copy to the dead-letter queue with the error, and mark the job Failed.
    /// The original is acked only after the copy is confirmed, so the message is never lost (worst case: duplicated,
    /// which the idempotent crawl tolerates). We don't use nack+requeue: on RabbitMQ 4 that neither delays nor counts.
    /// </summary>
    private async Task HandleFailureAsync(IChannel channel, BasicDeliverEventArgs delivery, CrawlJobRequested message,
        Exception ex, CancellationToken stoppingToken)
    {
        var attempt = GetAttempt(delivery.BasicProperties);
        var maxAttempts = retryOptions.Value.MaxAttempts;
        var action = MessageFailurePolicy.Decide(ex, attempt, maxAttempts);

        try
        {
            // Keep our own headers; drop the broker's bookkeeping (x-death, x-delivery-count, ...) so it doesn't pile up.
            var props = new BasicProperties(delivery.BasicProperties)
            {
                Headers = (delivery.BasicProperties.Headers ?? new Dictionary<string, object?>())
                    .Where(h => !h.Key.StartsWith("x-", StringComparison.Ordinal))
                    .ToDictionary(h => h.Key, h => h.Value),
            };

            if (action == FailureAction.Retry)
            {
                logger.LogWarning(ex, "Transient failure on attempt {Attempt}/{Max}; retrying in {Delay}s",
                    attempt, maxAttempts, RabbitMqTopology.RetryDelayMs / 1000);
                props.Headers[RabbitMqTopology.AttemptHeader] = attempt + 1;
                // Default exchange ("") routes straight to the queue named by the routing key.
                await publisher.PublishAsync("", RabbitMqTopology.RetryQueue, props, delivery.Body, stoppingToken);
            }
            else
            {
                logger.LogError(ex, "Giving up on attempt {Attempt} ({Kind}); sending to dead-letter queue",
                    attempt, MessageFailurePolicy.IsTransient(ex) ? "out of retries" : "non-transient error");
                props.Headers[RabbitMqTopology.AttemptHeader] = attempt;
                props.Headers[RabbitMqTopology.ErrorHeader] = Truncate(ex.Message, 500);
                props.Headers[RabbitMqTopology.ErrorTypeHeader] = ex.GetType().FullName;
                props.Headers[RabbitMqTopology.FailedAtHeader] = DateTimeOffset.UtcNow.ToString("O");
                await publisher.PublishAsync(RabbitMqTopology.DeadLetterExchange, RabbitMqTopology.DeadLetterRoutingKey,
                    props, delivery.Body, stoppingToken);
                await TryMarkJobFailedAsync(message.JobId, $"Gave up after {attempt} attempt(s): {ex.Message}");
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
        }
        catch (Exception publishError)
        {
            // Couldn't park the message (broker trouble too): pause briefly, then put it back as-is.
            // (The pause avoids a tight redelivery loop, since requeue has no built-in delay.)
            logger.LogError(publishError, "Could not route failed message; requeueing it as-is");
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { }
            try { await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true); }
            catch (Exception nackError) { logger.LogWarning(nackError, "Nack failed; the broker will redeliver when the channel closes"); }
        }
    }

    /// <summary>Best effort: if the DB is what's down, the job stays Running; replaying it from the DLQ later resumes it.</summary>
    private async Task TryMarkJobFailedAsync(Guid jobId, string reason)
    {
        try { await jobs.MarkFailedAsync(jobId, Truncate(reason, 1000), CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not mark job Failed"); }
    }

    /// <summary>
    /// 1 for a first delivery; the worker sets the header when it schedules a retry. Parsed defensively:
    /// a message published by hand (e.g. from the RabbitMQ UI) can carry it as a string (byte[]), or garbage.
    /// </summary>
    public static int GetAttempt(IReadOnlyBasicProperties props)
    {
        if (props.Headers?.TryGetValue(RabbitMqTopology.AttemptHeader, out var value) != true) return 1;
        var attempt = value switch
        {
            int i => i,
            long l => (int)Math.Clamp(l, 1, int.MaxValue),
            byte[] bytes when int.TryParse(System.Text.Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => 1,
        };
        return Math.Max(1, attempt);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static CrawlJobRequested? TryDeserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            var message = JsonSerializer.Deserialize<CrawlJobRequested>(body, RabbitMqJobQueue.Json);
            return message is { JobId: var id } && id != Guid.Empty ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
