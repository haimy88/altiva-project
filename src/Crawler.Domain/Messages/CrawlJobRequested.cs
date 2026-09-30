namespace Crawler.Domain.Messages;

/// <summary>
/// Published by the API when a job is created; consumed by the Worker.
/// Deliberately thin: the DB row is the source of truth for URL/limits, so the message only says "go".
/// </summary>
public sealed record CrawlJobRequested(
    Guid MessageId,
    Guid JobId,
    string CorrelationId,
    DateTimeOffset RequestedAt,
    int SchemaVersion = 1);

/// <summary>Publishing port. Implemented in Infrastructure (RabbitMQ).</summary>
public interface IJobQueue
{
    Task PublishAsync(CrawlJobRequested message, CancellationToken ct);
}
