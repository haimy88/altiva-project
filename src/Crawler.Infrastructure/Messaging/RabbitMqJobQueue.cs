using System.Text.Json;
using Crawler.Domain.Messages;
using RabbitMQ.Client;

namespace Crawler.Infrastructure.Messaging;

/// <summary>Publishes CrawlJobRequested as persistent JSON to the work queue (with broker confirm).</summary>
public sealed class RabbitMqJobQueue(RabbitMqPublisher publisher) : IJobQueue
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task PublishAsync(CrawlJobRequested message, CancellationToken ct)
    {
        var props = new BasicProperties
        {
            MessageId = message.MessageId.ToString(),
            CorrelationId = message.CorrelationId,
            ContentType = "application/json",
            Type = nameof(CrawlJobRequested),
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);

        return publisher.PublishAsync(RabbitMqTopology.Exchange, RabbitMqTopology.JobsRoutingKey, props, body, ct);
    }
}
