using System.Text.Json;
using Crawler.Domain.Messages;
using Crawler.Infrastructure.Messaging;
using Crawler.Worker.Crawling;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Crawler.Worker.Messaging;

/// <summary>
/// Consumes CrawlJobRequested from crawl.jobs. Manual acks: a message is only removed from the queue
/// after the job is fully processed, so a worker crash means RabbitMQ redelivers it (at-least-once).
/// </summary>
public sealed class CrawlJobConsumer(
    RabbitMqConnectionProvider connections,
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
            // TODO step 6: delayed retries for transient errors + counted attempts → DLQ.
            // (Plain requeue does NOT advance x-delivery-limit on RabbitMQ 4, so this can loop; don't rely on it.)
            logger.LogError(ex, "Job processing failed; requeueing");
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
        }
    }

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
