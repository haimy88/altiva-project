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

    /// <summary>Header carrying how many times this job message has been tried (set by the worker on retry).</summary>
    public const string AttemptHeader = "crawl-attempt";
    /// <summary>Headers added when a message is dead-lettered by the worker, for whoever inspects the DLQ.</summary>
    public const string ErrorHeader = "crawl-error";
    public const string ErrorTypeHeader = "crawl-error-type";
    public const string FailedAtHeader = "crawl-failed-at";

    public const string DeadLetterExchange = "crawl.dlx";
    public const string DeadLetterQueue = "crawl.jobs.dead";
    public const string DeadLetterRoutingKey = "crawl.job.dead";

    /// <summary>
    /// Safety net for crash loops: if the worker *process* dies while holding a message this many times
    /// (the broker counts deliveries to consumers that disappeared), RabbitMQ dead-letters it by itself.
    /// Normal failures are handled by the worker's own attempt counting (MessageFailurePolicy).
    /// </summary>
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
                // Don't lose a message on the retry → work-queue hop if the broker restarts mid-move.
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish",
            }, cancellationToken: ct);

        // Dead-letter queue: poison messages park here for inspection.
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: ct);
        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, DeadLetterRoutingKey, cancellationToken: ct);
    }
}
