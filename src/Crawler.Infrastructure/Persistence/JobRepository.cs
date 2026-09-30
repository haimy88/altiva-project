using Crawler.Domain.Jobs;
using Dapper;
using Npgsql;

namespace Crawler.Infrastructure.Persistence;

public sealed class JobRepository(NpgsqlDataSource db) : IJobRepository
{
    private const string JobColumns = """
        id AS Id, start_url AS StartUrl, start_host AS StartHost, max_depth AS MaxDepth, max_pages AS MaxPages,
        status AS Status, failure_reason AS FailureReason,
        created_at AS CreatedAt, started_at AS StartedAt, completed_at AS CompletedAt
        """;

    public async Task CreateAsync(CrawlJob job, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO crawl_jobs (id, start_url, start_host, max_depth, max_pages, status, created_at)
            VALUES (@Id, @StartUrl, @StartHost, @MaxDepth, @MaxPages, @Status, @CreatedAt)
            """;
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            job.Id, job.StartUrl, job.StartHost, job.MaxDepth, job.MaxPages,
            Status = job.Status.ToString(), job.CreatedAt,
        }, cancellationToken: ct));
    }

    public async Task<CrawlJob?> GetAsync(Guid jobId, CancellationToken ct)
    {
        var sql = $"SELECT {JobColumns} FROM crawl_jobs WHERE id = @jobId";
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(sql, new { jobId }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<JobProgress> GetProgressAsync(Guid jobId, CancellationToken ct)
    {
        const string sql = """
            SELECT count(*)                                   AS PagesDiscovered,
                   count(*) FILTER (WHERE status = 'Crawled') AS PagesCrawled,
                   count(*) FILTER (WHERE status = 'Failed')  AS PagesFailed,
                   count(*) FILTER (WHERE status = 'Skipped') AS PagesSkipped,
                   count(*) FILTER (WHERE status = 'Pending') AS PagesPending
            FROM pages WHERE job_id = @jobId
            """;
        await using var conn = await db.OpenConnectionAsync(ct);
        var p = await conn.QuerySingleAsync<ProgressRow>(new CommandDefinition(sql, new { jobId }, cancellationToken: ct));
        return new JobProgress((int)p.PagesDiscovered, (int)p.PagesCrawled, (int)p.PagesFailed, (int)p.PagesSkipped, (int)p.PagesPending);
    }

    /// <summary>
    /// Loads all pages + links of a job in one round trip (two result sets) and assembles the tree in memory.
    /// Fine because a job is capped at max_pages rows.
    /// </summary>
    public async Task<PageNode?> GetTreeAsync(Guid jobId, CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, parent_page_id AS ParentPageId, url AS Url, depth AS Depth, status AS Status,
                   http_status AS HttpStatus, domain_link_ratio AS DomainLinkRatio, error AS Error
            FROM pages WHERE job_id = @jobId
            ORDER BY depth, id;

            SELECT l.page_id AS PageId, l.target_url AS TargetUrl
            FROM page_links l JOIN pages p ON p.id = l.page_id
            WHERE p.job_id = @jobId
            ORDER BY l.page_id, l.target_url;
            """;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var results = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { jobId }, cancellationToken: ct));
        var pages = (await results.ReadAsync<PageRow>()).ToList();
        var links = (await results.ReadAsync<LinkRow>()).ToLookup(l => l.PageId, l => l.TargetUrl);

        return BuildTree(pages, links);
    }

    public async Task<Paged<CrawlJob>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        var sql = $"""
            SELECT {JobColumns} FROM crawl_jobs
            ORDER BY created_at DESC, id DESC
            LIMIT @pageSize OFFSET @offset;

            SELECT count(*) FROM crawl_jobs;
            """;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var results = await conn.QueryMultipleAsync(
            new CommandDefinition(sql, new { pageSize, offset = (long)(page - 1) * pageSize }, cancellationToken: ct));
        var items = (await results.ReadAsync<JobRow>()).Select(r => r.ToDomain()).ToList();
        var total = await results.ReadSingleAsync<long>();
        return new Paged<CrawlJob>(items, page, pageSize, (int)total);
    }

    /// <summary>
    /// One atomic statement: lock the row, cancel it only if not final, and return the status it ended with,
    /// so the answer can't be stale if the worker finishes the job at the same moment.
    /// </summary>
    public async Task<CancelResult?> TryCancelAsync(Guid jobId, CancellationToken ct)
    {
        const string sql = """
            WITH target AS (
                SELECT id, status FROM crawl_jobs WHERE id = @jobId FOR UPDATE
            ), canceled AS (
                UPDATE crawl_jobs j SET status = 'Canceled', completed_at = now()
                FROM target t
                WHERE j.id = t.id AND t.status IN ('Pending', 'Running')
                RETURNING j.id
            )
            SELECT EXISTS (SELECT 1 FROM canceled) AS Canceled, status AS Status FROM target
            """;
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<CancelRow>(new CommandDefinition(sql, new { jobId }, cancellationToken: ct));
        if (row is null) return null;
        return new CancelResult(row.Canceled, row.Canceled ? JobStatus.Canceled : Enum.Parse<JobStatus>(row.Status));
    }

    public async Task MarkFailedAsync(Guid jobId, string reason, CancellationToken ct)
    {
        const string sql = """
            UPDATE crawl_jobs SET status = 'Failed', failure_reason = @reason, completed_at = now()
            WHERE id = @jobId AND status IN ('Pending', 'Running')
            """;
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { jobId, reason }, cancellationToken: ct));
    }

    private static PageNode? BuildTree(List<PageRow> pages, ILookup<long, string> links)
    {
        var nodes = pages.ToDictionary(p => p.Id, p => new PageNode(
            p.Id, p.Url, p.Depth, Enum.Parse<PageStatus>(p.Status), p.HttpStatus, p.DomainLinkRatio, p.Error,
            links[p.Id].ToList(), []));

        PageNode? root = null;
        foreach (var p in pages) // all nodes already exist; this just links each one to its parent (children keep depth/id order)
        {
            if (p.ParentPageId is { } parentId && nodes.TryGetValue(parentId, out var parent))
                parent.Children.Add(nodes[p.Id]);
            else
                root ??= nodes[p.Id];
        }
        return root;
    }

    // Dapper row shapes (DB types) → mapped to domain records above.
    private sealed class JobRow
    {
        public Guid Id { get; init; }
        public string StartUrl { get; init; } = "";
        public string StartHost { get; init; } = "";
        public int MaxDepth { get; init; }
        public int MaxPages { get; init; }
        public string Status { get; init; } = "";
        public string? FailureReason { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }

        public CrawlJob ToDomain() => new(Id, StartUrl, StartHost, MaxDepth, MaxPages,
            Enum.Parse<JobStatus>(Status), FailureReason,
            ToUtc(CreatedAt), StartedAt is { } s ? ToUtc(s) : null, CompletedAt is { } c ? ToUtc(c) : null);

        private static DateTimeOffset ToUtc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
    }

    private sealed class CancelRow
    {
        public bool Canceled { get; init; }
        public string Status { get; init; } = "";
    }

    private sealed class ProgressRow
    {
        public long PagesDiscovered { get; init; }
        public long PagesCrawled { get; init; }
        public long PagesFailed { get; init; }
        public long PagesSkipped { get; init; }
        public long PagesPending { get; init; }
    }

    private sealed class PageRow
    {
        public long Id { get; init; }
        public long? ParentPageId { get; init; }
        public string Url { get; init; } = "";
        public int Depth { get; init; }
        public string Status { get; init; } = "";
        public int? HttpStatus { get; init; }
        public double? DomainLinkRatio { get; init; }
        public string? Error { get; init; }
    }

    private sealed class LinkRow
    {
        public long PageId { get; init; }
        public string TargetUrl { get; init; } = "";
    }
}
