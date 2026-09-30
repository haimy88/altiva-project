using Crawler.Infrastructure.Health;
using Crawler.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Crawler.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers Postgres, RabbitMQ and their health checks. Used by both Api and Worker.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Postgres'");

        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

        services.Configure<RabbitMqOptions>(config.GetSection(RabbitMqOptions.SectionName));
        services.AddSingleton<RabbitMqConnectionProvider>();

        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres")
            .AddCheck<RabbitMqHealthCheck>("rabbitmq");

        return services;
    }
}
