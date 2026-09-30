using RabbitMQ.Client;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Publishes with publisher confirms: PublishAsync returns only once RabbitMQ has safely stored the message,
/// and throws if it was rejected or couldn't be routed to any queue (mandatory).
/// </summary>
public sealed class RabbitMqPublisher(RabbitMqConnectionProvider connections)
{
    public async Task PublishAsync(string exchange, string routingKey, BasicProperties props, ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        var connection = await connections.GetConnectionAsync(ct);

        // Channels aren't thread-safe; one short-lived channel per publish is fine at our volume.
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);

        props.DeliveryMode = DeliveryModes.Persistent; // survives a broker restart
        await channel.BasicPublishAsync(exchange, routingKey, mandatory: true, basicProperties: props, body: body,
            cancellationToken: ct);
    }
}
