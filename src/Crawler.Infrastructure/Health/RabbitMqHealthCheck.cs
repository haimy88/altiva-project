using Crawler.Infrastructure.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Crawler.Infrastructure.Health;

public sealed class RabbitMqHealthCheck(RabbitMqConnectionProvider connections) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await connections.GetConnectionAsync(ct); // connects on first use
            return connections.IsOpen
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("RabbitMQ connection is down (reconnecting)");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cannot reach RabbitMQ", ex);
        }
    }
}
