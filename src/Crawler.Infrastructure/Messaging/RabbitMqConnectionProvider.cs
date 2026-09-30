using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// One long-lived RabbitMQ connection per process (connections are expensive; channels are cheap).
/// Created lazily on first use. After that it is never replaced: if the network drops, the client's
/// automatic recovery reconnects it and restores its channels and consumers. Replacing it ourselves
/// would throw those away.
/// </summary>
public sealed class RabbitMqConnectionProvider : IAsyncDisposable
{
    private readonly ConnectionFactory _factory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public RabbitMqConnectionProvider(IOptions<RabbitMqOptions> options)
    {
        var o = options.Value;
        _factory = new ConnectionFactory
        {
            HostName = o.Host,
            Port = o.Port,
            UserName = o.User,
            Password = o.Password,
            AutomaticRecoveryEnabled = true,
        };
    }

    /// <summary>True if the connection exists and is currently open (false while it is recovering).</summary>
    public bool IsOpen => _connection?.IsOpen == true;

    public async Task<IConnection> GetConnectionAsync(CancellationToken ct = default)
    {
        if (_connection is not null) return _connection;

        await _lock.WaitAsync(ct);
        try
        {
            // First connect: if RabbitMQ is down this throws and the next call tries again.
            return _connection ??= await _factory.CreateConnectionAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _lock.Dispose();
    }
}
