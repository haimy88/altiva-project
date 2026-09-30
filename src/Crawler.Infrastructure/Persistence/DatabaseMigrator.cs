using DbUp;
using Microsoft.Extensions.Logging;

namespace Crawler.Infrastructure.Persistence;

/// <summary>
/// Applies the numbered SQL scripts in Persistence/Migrations (embedded in this assembly) in order.
/// DbUp records applied scripts in a "schemaversions" table, so each script runs exactly once.
/// </summary>
public static class DatabaseMigrator
{
    public static bool Migrate(string connectionString, ILogger logger)
    {
        EnsureDatabase.For.PostgresqlDatabase(connectionString);

        var result = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(DatabaseMigrator).Assembly, name => name.Contains(".Migrations."))
            .WithTransactionPerScript()
            .LogTo(new DbUpLogger(logger))
            .Build()
            .PerformUpgrade();

        if (!result.Successful)
            logger.LogError(result.Error, "Database migration failed at {Script}", result.ErrorScript?.Name);
        else
            logger.LogInformation("Database is up to date ({Count} new scripts applied)", result.Scripts.Count());

        return result.Successful;
    }

    private sealed class DbUpLogger(ILogger logger) : DbUp.Engine.Output.IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) { }
        public void LogDebug(string format, params object[] args) { }
        public void LogInformation(string format, params object[] args) => logger.LogInformation(format, args);
        public void LogWarning(string format, params object[] args) => logger.LogWarning(format, args);
        public void LogError(string format, params object[] args) => logger.LogError(format, args);
        public void LogError(Exception ex, string format, params object[] args) => logger.LogError(ex, format, args);
    }
}
