using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Crawler.Infrastructure.Messaging;

/// <summary>Declares the RabbitMQ topology once at startup (both services).</summary>
public sealed class RabbitMqTopologyInitializer(RabbitMqConnectionProvider connections, ILogger<RabbitMqTopologyInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var connection = await connections.GetConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await RabbitMqTopology.DeclareAsync(channel, ct);
        logger.LogInformation("RabbitMQ topology declared");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
