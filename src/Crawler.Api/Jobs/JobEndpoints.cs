using Crawler.Api.Observability;
using Crawler.Domain.Jobs;
using Crawler.Domain.Links;
using Crawler.Domain.Messages;

namespace Crawler.Api.Jobs;

public static class JobEndpoints
{
    public const int MaxPageSize = 100;
    public const int MaxPage = 100_000;
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(5);

    public static void MapJobEndpoints(this WebApplication app)
    {
        var jobs = app.MapGroup("/api/jobs");

        jobs.MapPost("/", CreateJob);
        jobs.MapGet("/", ListJobs);
        jobs.MapGet("/{jobId:guid}", GetJob);
        jobs.MapGet("/{jobId:guid}/tree", GetTree);
        jobs.MapPost("/{jobId:guid}/cancel", CancelJob);
    }

    /// <summary>
    /// Save the job (Pending), then publish "crawl this". Returns 202: the work happens asynchronously.
    /// If the publish fails, the job is marked Failed so it never sits in Pending forever (see README: outbox).
    /// </summary>
    private static async Task<IResult> CreateJob(
        CreateJobRequest request, IJobRepository repo, IJobQueue queue, HttpContext http,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var startUrl = UrlNormalizer.NormalizeStartUrl(request.Url);
        var maxDepth = request.MaxDepth ?? CrawlJob.DefaultMaxDepth;

        var errors = new Dictionary<string, string[]>();
        if (startUrl is null)
            errors["url"] = ["Must be an absolute http(s) URL, e.g. https://example.com"];
        if (maxDepth is < 0 or > CrawlJob.MaxAllowedDepth)
            errors["maxDepth"] = [$"Must be between 0 and {CrawlJob.MaxAllowedDepth}"];
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        var job = CrawlJob.CreateNew(startUrl!, maxDepth, CrawlJob.DefaultMaxPages);
        await repo.CreateAsync(job, ct);

        var logger = loggers.CreateLogger("Crawler.Api.Jobs");
        using var _ = logger.BeginScope("JobId:{JobId}", job.Id);
        try
        {
            // Once the job row exists we must finish publishing even if the client disconnects
            // (otherwise the job could be marked Failed while its message was actually delivered).
            // Bounded by a timeout instead, e.g. if the broker blocks publishers under a memory alarm.
            using var publishTimeout = new CancellationTokenSource(PublishTimeout);
            var message = new CrawlJobRequested(Guid.NewGuid(), job.Id, CorrelationId.Get(http), DateTimeOffset.UtcNow);
            await queue.PublishAsync(message, publishTimeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enqueue job");
            await repo.MarkFailedAsync(job.Id, "Could not enqueue job: message broker unavailable", CancellationToken.None);
            return Results.Problem("Job was saved but could not be queued. Please retry.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation("Job created for {Url} (maxDepth {MaxDepth})", job.StartUrl, job.MaxDepth);
        return Results.Accepted($"/api/jobs/{job.Id}", new CreateJobResponse(job.Id));
    }

    private static async Task<IResult> GetJob(Guid jobId, IJobRepository repo, CancellationToken ct)
    {
        var job = await repo.GetAsync(jobId, ct);
        if (job is null) return JobNotFound(jobId);

        var progress = await repo.GetProgressAsync(jobId, ct);
        return Results.Ok(JobSummaryResponse.From(job, progress));
    }

    private static async Task<IResult> GetTree(Guid jobId, IJobRepository repo, CancellationToken ct)
    {
        var job = await repo.GetAsync(jobId, ct);
        if (job is null) return JobNotFound(jobId);

        // Returned at any time: while running it's the partial tree so far.
        var root = await repo.GetTreeAsync(jobId, ct);
        return Results.Ok(new JobTreeResponse(jobId, job.Status.ToString(), root));
    }

    private static async Task<IResult> ListJobs(IJobRepository repo, CancellationToken ct, int page = 1, int pageSize = 20)
    {
        if (page is < 1 or > MaxPage || pageSize is < 1 or > MaxPageSize)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["paging"] = [$"page must be between 1 and {MaxPage}, pageSize between 1 and {MaxPageSize}"],
            });

        var result = await repo.ListAsync(page, pageSize, ct);
        return Results.Ok(new Paged<JobListItem>(
            result.Items.Select(JobListItem.From).ToList(), result.Page, result.PageSize, result.TotalCount));
    }

    private static async Task<IResult> CancelJob(Guid jobId, IJobRepository repo, CancellationToken ct)
    {
        var result = await repo.TryCancelAsync(jobId, ct);
        return result switch
        {
            null => JobNotFound(jobId),
            { Canceled: true } => Results.Accepted($"/api/jobs/{jobId}"),
            _ => Results.Problem($"Job is already {result.Status}", statusCode: StatusCodes.Status409Conflict),
        };
    }

    private static IResult JobNotFound(Guid jobId) =>
        Results.Problem($"Job {jobId} not found", statusCode: StatusCodes.Status404NotFound);
}
