using System.Text.Json.Serialization;
using Crawler.Api.Jobs;
using Crawler.Api.Observability;
using Crawler.Infrastructure;
using Crawler.Infrastructure.Health;
using Crawler.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails(); // consistent JSON error bodies (RFC 7807), incl. unhandled exceptions
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter())); // enums as "Pending", not 0

// The React dev server runs on another origin.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders(CorrelationId.Header, "Location")));

var app = builder.Build();

// One-shot mode: apply DB migrations and exit (run by the "migrate" service in docker-compose,
// before the API and Worker start, so the two services never race to create the schema).
if (args.Contains("--migrate"))
{
    var ok = DatabaseMigrator.Migrate(builder.Configuration.GetConnectionString("Postgres")!, app.Logger);
    return ok ? 0 : 1;
}

app.UseCorrelationId(); // first, so even unhandled-exception logs and 500 responses carry it
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();

app.MapCrawlerHealthEndpoints();
app.MapJobEndpoints();

app.Logger.LogInformation("Crawler.Api started");
app.Run();
return 0;
