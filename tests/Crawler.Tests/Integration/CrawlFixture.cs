using Crawler.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Crawler.Tests.Integration;

/// <summary>
/// Shared by the integration tests: a throwaway Postgres (Testcontainers, migrated with our real scripts)
/// and a local web server serving the HTML fixtures in Integration/Fixtures/site. Requires Docker.
/// </summary>
public sealed class CrawlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private WebApplication? _site;

    public NpgsqlDataSource Db { get; private set; } = null!;
    public Uri SiteUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var connectionString = _postgres.GetConnectionString();
        if (!DatabaseMigrator.Migrate(connectionString, NullLogger.Instance))
            throw new InvalidOperationException("Migrations failed");
        Db = NpgsqlDataSource.Create(connectionString);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "Integration", "Fixtures", "site"),
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // any free port
        builder.Logging.ClearProviders();
        _site = builder.Build();
        _site.UseDefaultFiles(); // /blog/ → blog/index.html
        _site.UseStaticFiles();  // .html → text/html, .json → application/json, missing → 404
        await _site.StartAsync();
        SiteUrl = new Uri(_site.Urls.First() + "/");
    }

    public async Task DisposeAsync()
    {
        if (_site is not null) await _site.DisposeAsync();
        await Db.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
