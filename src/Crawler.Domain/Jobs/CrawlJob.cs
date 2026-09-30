namespace Crawler.Domain.Jobs;

public sealed record CrawlJob(
    Guid Id,
    string StartUrl,
    string StartHost,
    int MaxDepth,
    int MaxPages,
    JobStatus Status,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt)
{
    public const int DefaultMaxDepth = 2;
    public const int MaxAllowedDepth = 5;
    public const int DefaultMaxPages = 200;

    public static CrawlJob CreateNew(Uri startUrl, int maxDepth, int maxPages) => new(
        Id: Guid.NewGuid(),
        StartUrl: startUrl.AbsoluteUri,
        StartHost: startUrl.Host,
        MaxDepth: maxDepth,
        MaxPages: maxPages,
        Status: JobStatus.Pending,
        FailureReason: null,
        CreatedAt: DateTimeOffset.UtcNow,
        StartedAt: null,
        CompletedAt: null);
}

/// <summary>Live progress, computed from the pages table.</summary>
public sealed record JobProgress(int PagesDiscovered, int PagesCrawled, int PagesFailed, int PagesSkipped, int PagesPending);

/// <summary>One crawled/queued page in the result tree. Children = pages first discovered on this page.</summary>
public sealed record PageNode(
    long Id,
    string Url,
    int Depth,
    PageStatus Status,
    int? HttpStatus,
    double? DomainLinkRatio,
    string? Error,
    IReadOnlyList<string> OutgoingLinks,
    List<PageNode> Children);

public sealed record CancelResult(bool Canceled, JobStatus Status);

public sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
