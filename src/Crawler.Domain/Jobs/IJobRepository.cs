namespace Crawler.Domain.Jobs;

/// <summary>Persistence port for jobs. Implemented in Infrastructure (Postgres).</summary>
public interface IJobRepository
{
    Task CreateAsync(CrawlJob job, CancellationToken ct);
    Task<CrawlJob?> GetAsync(Guid jobId, CancellationToken ct);
    Task<JobProgress> GetProgressAsync(Guid jobId, CancellationToken ct);
    Task<PageNode?> GetTreeAsync(Guid jobId, CancellationToken ct);
    Task<Paged<CrawlJob>> ListAsync(int page, int pageSize, CancellationToken ct);

    /// <summary>Pending/Running → Canceled. Null if the job doesn't exist; Canceled=false if it was already final.</summary>
    Task<CancelResult?> TryCancelAsync(Guid jobId, CancellationToken ct);

    Task MarkFailedAsync(Guid jobId, string reason, CancellationToken ct);
}
