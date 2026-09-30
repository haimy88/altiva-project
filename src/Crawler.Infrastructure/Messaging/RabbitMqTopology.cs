using RabbitMQ.Client;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// All exchanges/queues in one place. Declarations are idempotent, so both the API (publisher)
/// and the Worker (consumer) call DeclareAsync on startup; whoever starts first creates them.
///
///   API ──publish──▶ [crawl] ──"crawl.job"──▶ (crawl.jobs) ──▶ Worker
///                                                  │ rejected / delivery limit hit
///                                                  ▼
///                  [crawl.dlx] ──"crawl.job.dead"──▶ (crawl.jobs.dead)   ← poison messages, inspected by hand
///
///   Worker ──transient failure──▶ (crawl.jobs.retry, TTL) ──expires──▶ [crawl] ──▶ (crawl.jobs)   ← delayed retry
/// </summary>
public static class RabbitMqTopology
{
    public const string Exchange = "crawl";
    public const string JobsQueue = "crawl.jobs";
    public const string JobsRoutingKey = "crawl.job";

    public const string RetryQueue = "crawl.jobs.retry";
    public const int RetryDelayMs = 10_000;

    public const string DeadLetterExchange = "crawl.dlx";
    public const string DeadLetterQueue = "crawl.jobs.dead";
    public const string DeadLetterRoutingKey = "crawl.job.dead";

    /// <summary>Safety net: a message redelivered this many times (e.g. worker keeps crashing on it) goes to the DLQ.</summary>
    public const int DeliveryLimit = 5;

    public static async Task DeclareAsync(IChannel channel, CancellationToken ct = default)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);

        // Main work queue. Quorum = replicated, durable, and supports a native delivery limit.
        await channel.QueueDeclareAsync(JobsQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = DeliveryLimit,
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = DeadLetterRoutingKey,
            }, cancellationToken: ct);
        await channel.QueueBindAsync(JobsQueue, Exchange, JobsRoutingKey, cancellationToken: ct);

        // Retry "waiting room": no consumers; messages expire after the TTL and are dead-lettered back to the main queue.
        await channel.QueueDeclareAsync(RetryQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-message-ttl"] = RetryDelayMs,
                ["x-dead-letter-exchange"] = Exchange,
                ["x-dead-letter-routing-key"] = JobsRoutingKey,
            }, cancellationToken: ct);

        // Dead-letter queue: poison messages park here for inspection.
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: ct);
        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, DeadLetterRoutingKey, cancellationToken: ct);
    }
}
