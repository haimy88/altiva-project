using Crawler.Infrastructure;
using Crawler.Infrastructure.Health;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.MapCrawlerHealthEndpoints();

app.Logger.LogInformation("Crawler.Api started");
app.Run();
