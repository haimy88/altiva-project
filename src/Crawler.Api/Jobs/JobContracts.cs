using Crawler.Domain.Jobs;

namespace Crawler.Api.Jobs;

// HTTP request/response shapes (kept separate from domain records so the API contract can evolve independently).

public sealed record CreateJobRequest(string? Url, int? MaxDepth);

public sealed record CreateJobResponse(Guid JobId);

public sealed record JobSummaryResponse(
    Guid JobId,
    string Url,
    string Status,
    int MaxDepth,
    int MaxPages,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    JobProgress Progress)
{
    public static JobSummaryResponse From(CrawlJob job, JobProgress progress) => new(
        job.Id, job.StartUrl, job.Status.ToString(), job.MaxDepth, job.MaxPages,
        job.CreatedAt, job.StartedAt, job.CompletedAt, job.FailureReason, progress);
}

public sealed record JobListItem(
    Guid JobId, string Url, string Status, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt)
{
    public static JobListItem From(CrawlJob job) =>
        new(job.Id, job.StartUrl, job.Status.ToString(), job.CreatedAt, job.StartedAt, job.CompletedAt);
}

public sealed record JobTreeResponse(Guid JobId, string Status, PageNode? Root);
