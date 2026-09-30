namespace Crawler.Api.Observability;

/// <summary>
/// Every request gets a correlation id: taken from the X-Correlation-Id header if the caller sent one,
/// otherwise generated. It's echoed back in the response, added to every log line (log scope),
/// and copied into the RabbitMQ message so the Worker's logs for that job share it.
/// </summary>
public static class CorrelationId
{
    public const string Header = "X-Correlation-Id";

    public static string Get(HttpContext http) => (string)http.Items[Header]!;

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var id = http.Request.Headers[Header].FirstOrDefault() is { Length: > 0 and <= 64 } incoming
                ? incoming
                : Guid.NewGuid().ToString();

            http.Items[Header] = id;
            // Set just before the response is sent: the exception handler clears headers when it rewrites a 500.
            http.Response.OnStarting(() => { http.Response.Headers[Header] = id; return Task.CompletedTask; });

            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Crawler.Api");
            using (logger.BeginScope("CorrelationId:{CorrelationId}", id))
            {
                await next();
            }
        });
}
