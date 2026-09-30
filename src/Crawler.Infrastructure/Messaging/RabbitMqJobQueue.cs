using System.Text.Json;
using Crawler.Domain.Messages;
using RabbitMQ.Client;

namespace Crawler.Infrastructure.Messaging;

/// <summary>Publishes CrawlJobRequested as persistent JSON and waits for the broker's confirm.</summary>
public sealed class RabbitMqJobQueue(RabbitMqConnectionProvider connections) : IJobQueue
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(CrawlJobRequested message, CancellationToken ct)
    {
        var connection = await connections.GetConnectionAsync(ct);

        // Channels aren't thread-safe; one short-lived channel per publish is fine at our volume.
        // Publisher confirms: PublishAsync only returns once RabbitMQ has safely stored the message (throws otherwise).
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);

        var props = new BasicProperties
        {
            MessageId = message.MessageId.ToString(),
            CorrelationId = message.CorrelationId,
            ContentType = "application/json",
            Type = nameof(CrawlJobRequested),
            DeliveryMode = DeliveryModes.Persistent, // survives a broker restart
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);

        await channel.BasicPublishAsync(RabbitMqTopology.Exchange, RabbitMqTopology.JobsRoutingKey,
            mandatory: true, basicProperties: props, body: body, cancellationToken: ct);
    }
}
