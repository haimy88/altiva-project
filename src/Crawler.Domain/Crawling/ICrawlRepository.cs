using Crawler.Domain.Jobs;

namespace Crawler.Domain.Crawling;

/// <summary>A page waiting to be crawled (a Pending row in the pages table, which doubles as the crawl queue).</summary>
public sealed record PendingPage(long Id, string Url, int Depth);

/// <summary>Everything learned from one successfully fetched HTML page, saved atomically.</summary>
public sealed record CrawledPage(
    long PageId,
    int HttpStatus,
    double DomainLinkRatio,
    IReadOnlyList<string> OutgoingLinks,   // all normalized links (internal + external) → page_links
    IReadOnlyList<string> ChildrenToQueue, // same-domain links to crawl next (empty at max depth) → new Pending pages
    int ChildDepth);

/// <summary>Worker-side persistence port. Every write is idempotent, so a redelivered message is safe.</summary>
public interface ICrawlRepository
{
    /// <summary>Pending/Running → Running (sets started_at once). False if the job is final or missing: skip it.</summary>
    Task<bool> TryStartJobAsync(Guid jobId, CancellationToken ct);

    /// <summary>Inserts the start page (depth 0) if it isn't there yet.</summary>
    Task EnsureRootPageAsync(Guid jobId, string url, CancellationToken ct);

    /// <summary>Next page to crawl, breadth-first (shallowest, then oldest). Null when nothing is left.</summary>
    Task<PendingPage?> GetNextPendingPageAsync(Guid jobId, CancellationToken ct);

    Task<JobStatus?> GetJobStatusAsync(Guid jobId, CancellationToken ct);

    /// <summary>
    /// One transaction: mark the page Crawled, store its links, queue its same-domain children
    /// (skipping URLs already in the job, and never exceeding the job's max_pages).
    /// </summary>
    Task SaveCrawledPageAsync(Guid jobId, CrawledPage page, CancellationToken ct);

    Task MarkPageAsync(long pageId, PageStatus status, int? httpStatus, string? error, CancellationToken ct);

    /// <summary>Marks every still-Pending page of the job Skipped (used when a job hits its time limit).</summary>
    Task SkipRemainingPagesAsync(Guid jobId, string reason, CancellationToken ct);

    Task CompleteJobAsync(Guid jobId, CancellationToken ct);
}
