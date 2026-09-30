using Crawler.Domain.Crawling;
using Crawler.Domain.Jobs;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace Crawler.Infrastructure.Persistence;

public sealed class CrawlRepository(NpgsqlDataSource db) : ICrawlRepository
{
    public async Task<bool> TryStartJobAsync(Guid jobId, CancellationToken ct)
    {
        // Running → Running is allowed: that's a redelivered message resuming a crawl after a worker crash.
        const string sql = """
            UPDATE crawl_jobs SET status = 'Running', started_at = COALESCE(started_at, now())
            WHERE id = @jobId AND status IN ('Pending', 'Running')
            """;
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { jobId }, cancellationToken: ct)) == 1;
    }

    public async Task EnsureRootPageAsync(Guid jobId, string url, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO pages (job_id, url, depth) VALUES (@jobId, @url, 0)
            ON CONFLICT (job_id, url) DO NOTHING
            """;
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { jobId, url }, cancellationToken: ct));
    }

    public async Task<PendingPage?> GetNextPendingPageAsync(Guid jobId, CancellationToken ct)
    {
        // Served by the partial index ix_pages_next_pending.
        const string sql = """
            SELECT id AS Id, url AS Url, depth AS Depth FROM pages
            WHERE job_id = @jobId AND status = 'Pending'
            ORDER BY depth, id
            LIMIT 1
            """;
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PendingPage>(new CommandDefinition(sql, new { jobId }, cancellationToken: ct));
    }

    public async Task<JobStatus?> GetJobStatusAsync(Guid jobId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var status = await conn.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition("SELECT status FROM crawl_jobs WHERE id = @jobId", new { jobId }, cancellationToken: ct));
        return status is null ? null : Enum.Parse<JobStatus>(status);
    }

    public async Task SaveCrawledPageAsync(Guid jobId, CrawledPage page, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 1. The page itself. "AND status = 'Pending'" makes a replay a no-op. If another copy of this job already
        //    finished the page (duplicate delivery running concurrently), stop: its links/children are already saved.
        var updated = await ExecuteAsync(conn, tx, """
            UPDATE pages SET status = 'Crawled', http_status = @httpStatus, domain_link_ratio = @ratio,
                             error = NULL, crawled_at = now()
            WHERE id = @pageId AND status = 'Pending'
            """, ct,
            ("pageId", page.PageId, NpgsqlDbType.Bigint),
            ("httpStatus", page.HttpStatus, NpgsqlDbType.Integer),
            ("ratio", page.DomainLinkRatio, NpgsqlDbType.Double));
        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return;
        }

        // 2. Edges: every outgoing link. Duplicates (replays) are ignored by the primary key.
        await ExecuteAsync(conn, tx, """
            INSERT INTO page_links (page_id, target_url)
            SELECT @pageId, u FROM unnest(@urls) AS u
            ON CONFLICT DO NOTHING
            """, ct,
            ("pageId", page.PageId, NpgsqlDbType.Bigint),
            ("urls", page.OutgoingLinks.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text));

        // 3. Children to crawl: only URLs not already in this job, in link order, capped so the job
        //    never exceeds max_pages. The job row is locked FIRST, in its own statement: under READ COMMITTED
        //    the next statement then gets a fresh snapshot, so its count includes other writers' committed pages.
        if (page.ChildrenToQueue.Count > 0)
        {
            await ExecuteAsync(conn, tx, "SELECT 1 FROM crawl_jobs WHERE id = @jobId FOR UPDATE", ct,
                ("jobId", jobId, NpgsqlDbType.Uuid));

            await ExecuteAsync(conn, tx, """
                WITH room AS (SELECT GREATEST(0, (SELECT max_pages FROM crawl_jobs WHERE id = @jobId) - count(*)) AS n
                              FROM pages WHERE job_id = @jobId)
                INSERT INTO pages (job_id, parent_page_id, url, depth)
                SELECT @jobId, @pageId, t.u, @depth
                FROM unnest(@urls) WITH ORDINALITY AS t(u, ord)
                WHERE NOT EXISTS (SELECT 1 FROM pages p WHERE p.job_id = @jobId AND p.url = t.u)
                ORDER BY t.ord
                LIMIT (SELECT n FROM room)
                ON CONFLICT (job_id, url) DO NOTHING
                """, ct,
                ("jobId", jobId, NpgsqlDbType.Uuid),
                ("pageId", page.PageId, NpgsqlDbType.Bigint),
                ("depth", page.ChildDepth, NpgsqlDbType.Integer),
                ("urls", page.ChildrenToQueue.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text));
        }

        await tx.CommitAsync(ct);
    }

    public async Task MarkPageAsync(long pageId, PageStatus status, int? httpStatus, string? error, CancellationToken ct)
    {
        const string sql = """
            UPDATE pages SET status = @status, http_status = @httpStatus, error = @error, crawled_at = now()
            WHERE id = @pageId AND status = 'Pending'
            """;
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { pageId, status = status.ToString(), httpStatus, error = Truncate(error, 1000) }, cancellationToken: ct));
    }

    public async Task SkipRemainingPagesAsync(Guid jobId, string reason, CancellationToken ct)
    {
        const string sql = """
            UPDATE pages SET status = 'Skipped', error = @reason, crawled_at = now()
            WHERE job_id = @jobId AND status = 'Pending'
            """;
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { jobId, reason }, cancellationToken: ct));
    }

    public async Task CompleteJobAsync(Guid jobId, CancellationToken ct)
    {
        // The root page failing (e.g. DNS error, 404 on the start URL) fails the whole job; otherwise Completed.
        const string sql = """
            UPDATE crawl_jobs j
            SET status         = CASE WHEN root.status = 'Failed' THEN 'Failed' ELSE 'Completed' END,
                failure_reason = CASE WHEN root.status = 'Failed' THEN 'Start URL could not be crawled: ' || COALESCE(root.error, 'unknown error') END,
                completed_at   = now()
            FROM pages root
            WHERE j.id = @jobId AND j.status = 'Running'
              AND root.job_id = j.id AND root.depth = 0
            """;
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { jobId }, cancellationToken: ct));
    }

    // Raw Npgsql for array parameters (Dapper would expand arrays into "(@p1, @p2, ...)" lists).
    private static async Task<int> ExecuteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object Value, NpgsqlDbType Type)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value, type) in parameters)
            cmd.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
