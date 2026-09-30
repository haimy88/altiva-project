using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Crawler.Infrastructure.Health;

public static class HealthEndpoints
{
    /// <summary>
    /// /health/live  - process is up (no dependency checks).
    /// /health       - process is up AND can reach Postgres + RabbitMQ; returns per-dependency status.
    /// </summary>
    public static WebApplication MapCrawlerHealthEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteJsonAsync });
        return app;
    }

    private static Task WriteJsonAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), error = e.Value.Exception?.Message }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }
}
