using Crawler.Infrastructure;
using Crawler.Infrastructure.Health;
using Crawler.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

// One-shot mode: apply DB migrations and exit (run by the "migrate" service in docker-compose,
// before the API and Worker start, so the two services never race to create the schema).
if (args.Contains("--migrate"))
{
    var ok = DatabaseMigrator.Migrate(builder.Configuration.GetConnectionString("Postgres")!, app.Logger);
    return ok ? 0 : 1;
}

app.MapCrawlerHealthEndpoints();

app.Logger.LogInformation("Crawler.Api started");
app.Run();
return 0;
