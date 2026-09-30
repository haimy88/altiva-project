using System.Net.Sockets;
using Crawler.Infrastructure.Messaging;
using Crawler.Worker.Messaging;
using Npgsql;
using RabbitMQ.Client;

namespace Crawler.Tests.Messaging;

public class MessageFailurePolicyTests
{
    private const int MaxAttempts = 5;

    public static TheoryData<Exception> TransientErrors => new()
    {
        new NpgsqlException("connection refused", new SocketException((int)SocketError.ConnectionRefused)),
        new TimeoutException("timed out"),
        new IOException("connection reset"),
        new SocketException((int)SocketError.HostUnreachable),
        new InvalidOperationException("wrapper", new TimeoutException("inner timeout")), // transient cause, wrapped
    };

    public static TheoryData<Exception> PermanentErrors => new()
    {
        new InvalidOperationException("bug"),
        new NullReferenceException(),
        new FormatException("bad data"),
        new ArgumentException("bad argument"),
    };

    [Theory, MemberData(nameof(TransientErrors))]
    public void Transient_errors_are_retried_while_attempts_remain(Exception ex)
    {
        Assert.True(MessageFailurePolicy.IsTransient(ex));
        Assert.Equal(FailureAction.Retry, MessageFailurePolicy.Decide(ex, attempt: 1, MaxAttempts));
        Assert.Equal(FailureAction.Retry, MessageFailurePolicy.Decide(ex, attempt: MaxAttempts - 1, MaxAttempts));
    }

    [Theory, MemberData(nameof(TransientErrors))]
    public void Transient_errors_are_dead_lettered_on_the_last_attempt(Exception ex) =>
        Assert.Equal(FailureAction.DeadLetter, MessageFailurePolicy.Decide(ex, attempt: MaxAttempts, MaxAttempts));

    [Theory, MemberData(nameof(PermanentErrors))]
    public void Permanent_errors_are_dead_lettered_immediately(Exception ex)
    {
        Assert.False(MessageFailurePolicy.IsTransient(ex));
        Assert.Equal(FailureAction.DeadLetter, MessageFailurePolicy.Decide(ex, attempt: 1, MaxAttempts));
    }

    [Fact]
    public void MaxAttempts_of_1_means_no_retries() =>
        Assert.Equal(FailureAction.DeadLetter, MessageFailurePolicy.Decide(new TimeoutException(), attempt: 1, maxAttempts: 1));

    [Fact]
    public void Attempt_is_1_without_header()
    {
        Assert.Equal(1, CrawlJobConsumer.GetAttempt(new BasicProperties()));
        Assert.Equal(1, CrawlJobConsumer.GetAttempt(new BasicProperties { Headers = new Dictionary<string, object?>() }));
    }

    [Theory]
    [InlineData("not a number")]
    [InlineData(0)]
    [InlineData(-4)]
    public void Garbage_or_invalid_attempt_header_counts_as_1(object headerValue)
    {
        var props = new BasicProperties { Headers = new Dictionary<string, object?> { [RabbitMqTopology.AttemptHeader] = headerValue } };
        Assert.Equal(1, CrawlJobConsumer.GetAttempt(props));
    }

    [Fact]
    public void Attempt_header_sent_as_string_bytes_is_parsed() // e.g. published by hand from the RabbitMQ UI
    {
        var props = new BasicProperties { Headers = new Dictionary<string, object?> { [RabbitMqTopology.AttemptHeader] = "4"u8.ToArray() } };
        Assert.Equal(4, CrawlJobConsumer.GetAttempt(props));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(3L)] // RabbitMQ may hand integers back as long
    public void Attempt_is_read_from_header(object headerValue)
    {
        var props = new BasicProperties { Headers = new Dictionary<string, object?> { [RabbitMqTopology.AttemptHeader] = headerValue } };
        Assert.Equal(3, CrawlJobConsumer.GetAttempt(props));
    }
}
