using Crawler.Infrastructure;
using Crawler.Infrastructure.Health;
using Crawler.Worker.Crawling;
using Crawler.Worker.Messaging;
using Microsoft.Extensions.Http.Resilience;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);

// HTTP client for crawling: per-attempt timeout, total timeout, and retries with exponential backoff
// for transient failures only (network errors, 5xx, 408, 429). 404 etc. are not retried.
builder.Services.AddHttpClient<IPageFetcher, HttpPageFetcher>(http =>
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AltevaCrawler/1.0 (+take-home assignment)");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.1");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2), // pick up DNS changes
    })
    .AddStandardResilienceHandler(o =>
    {
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        o.Retry.MaxRetryAttempts = 2;
        o.Retry.Delay = TimeSpan.FromMilliseconds(500);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60); // must be >= 2x attempt timeout
    })
    // Separate retry/circuit-breaker state per host: one flaky site must not trip fetches for every other job.
    .SelectPipelineByAuthority();

builder.Services.AddTransient<CrawlJobProcessor>();
builder.Services.AddHostedService<CrawlJobConsumer>();

var app = builder.Build();

app.MapCrawlerHealthEndpoints();

app.Logger.LogInformation("Crawler.Worker started");
app.Run();
