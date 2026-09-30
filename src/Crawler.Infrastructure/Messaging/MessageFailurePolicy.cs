using System.Net.Sockets;
using Npgsql;
using RabbitMQ.Client.Exceptions;

namespace Crawler.Infrastructure.Messaging;

public enum FailureAction { Retry, DeadLetter }

/// <summary>Config section "Messaging:Retry". An operational setting, not something clients send.</summary>
public sealed class MessageRetryOptions
{
    public const string SectionName = "Messaging:Retry";

    /// <summary>Total tries including the first. Default 5 = 1 + 4 retries, 10s apart (queue TTL) ≈ 40s of patience.</summary>
    public int MaxAttempts { get; set; } = 5;
}

/// <summary>
/// Decides what happens to a message whose processing threw.
///   Transient (worth retrying later): the DB or broker is unreachable, timeouts, network I/O errors.
///   Anything else (a bug, bad data) won't fix itself by waiting → dead-letter immediately.
/// Transient failures are retried until MaxAttempts (configurable), then dead-lettered too.
/// (Per-page HTTP errors never get here: the fetcher turns them into a Failed page.)
/// </summary>
public static class MessageFailurePolicy
{
    public static FailureAction Decide(Exception ex, int attempt, int maxAttempts) =>
        IsTransient(ex) && attempt < maxAttempts ? FailureAction.Retry : FailureAction.DeadLetter;

    public static bool IsTransient(Exception ex) => ex switch
    {
        NpgsqlException npgsql => npgsql.IsTransient, // connection refused/reset, timeouts, DB restarting, ...
        TimeoutException or IOException or SocketException => true,
        BrokerUnreachableException or AlreadyClosedException => true,
        _ => ex.InnerException is { } inner && IsTransient(inner),
    };
}
