using System.Diagnostics;
using Crawler.Domain.Crawling;
using Crawler.Domain.Jobs;
using Crawler.Domain.Links;

namespace Crawler.Worker.Crawling;

/// <summary>
/// Runs one crawl job to completion, breadth-first, using the pages table as the queue:
///   take next Pending page → fetch → extract + normalize links → ratio → save page, links and new children → repeat.
/// Safe to run again for the same job (redelivered message): finished pages are no longer Pending, so it resumes.
/// </summary>
public sealed class CrawlJobProcessor(
    IJobRepository jobs,
    ICrawlRepository crawl,
    IPageFetcher fetcher,
    ILogger<CrawlJobProcessor> logger)
{
    /// <summary>
    /// Safety limit on one job's wall-clock time. The job's message stays unacked while it runs, and RabbitMQ
    /// force-closes a consumer holding a message longer than its ack timeout (30 min by default), so stay well below.
    /// </summary>
    public static readonly TimeSpan MaxJobDuration = TimeSpan.FromMinutes(20);

    public async Task ProcessAsync(Guid jobId, CancellationToken ct)
    {
        var job = await jobs.GetAsync(jobId, ct);
        if (job is null)
        {
            logger.LogWarning("Job not found; nothing to do");
            return;
        }

        if (!await crawl.TryStartJobAsync(jobId, ct))
        {
            logger.LogInformation("Job is already {Status}; skipping", await crawl.GetJobStatusAsync(jobId, ct));
            return;
        }

        logger.LogInformation("Crawl started for {Url} (maxDepth {MaxDepth}, maxPages {MaxPages})",
            job.StartUrl, job.MaxDepth, job.MaxPages);
        await crawl.EnsureRootPageAsync(jobId, job.StartUrl, ct);

        var crawled = 0;
        var clock = Stopwatch.StartNew();
        while (await crawl.GetNextPendingPageAsync(jobId, ct) is { } page)
        {
            if (await crawl.GetJobStatusAsync(jobId, ct) == JobStatus.Canceled)
            {
                logger.LogInformation("Job canceled after {Count} pages; stopping", crawled);
                await crawl.SkipRemainingPagesAsync(jobId, "Job canceled", ct);
                return;
            }

            if (clock.Elapsed > MaxJobDuration)
            {
                // Like max pages: a safety limit, so the job completes with what it has.
                logger.LogWarning("Job time limit {Limit} reached after {Count} pages; skipping the rest", MaxJobDuration, crawled);
                await crawl.SkipRemainingPagesAsync(jobId, $"Job time limit ({MaxJobDuration.TotalMinutes:0} min) reached", ct);
                break;
            }

            await CrawlPageAsync(job, page, ct);
            crawled++;
        }

        await crawl.CompleteJobAsync(jobId, ct);
        logger.LogInformation("Crawl finished: {Count} pages processed", crawled);
    }

    private async Task CrawlPageAsync(CrawlJob job, PendingPage page, CancellationToken ct)
    {
        using var _ = logger.BeginScope("PageId:{PageId}", page.Id);

        var result = await fetcher.FetchAsync(new Uri(page.Url), job.StartHost, ct);
        switch (result)
        {
            case FetchResult.Html html:
                await ProcessHtmlPageAsync(job, page, html, ct);
                break;

            case FetchResult.Skipped skipped:
                logger.LogInformation("Skipped {Url}: {Reason}", page.Url, skipped.Reason);
                await crawl.MarkPageAsync(page.Id, PageStatus.Skipped, skipped.HttpStatus, skipped.Reason, ct);
                break;

            case FetchResult.Failed failed:
                logger.LogWarning("Failed {Url}: {Reason}", page.Url, failed.Reason);
                await crawl.MarkPageAsync(page.Id, PageStatus.Failed, failed.HttpStatus, failed.Reason, ct);
                break;
        }
    }

    private async Task ProcessHtmlPageAsync(CrawlJob job, PendingPage page, FetchResult.Html html, CancellationToken ct)
    {
        var (baseUrl, hrefs) = HtmlLinkExtractor.Extract(html.Content, html.FinalUrl);
        var links = UrlNormalizer.NormalizeAll(hrefs, baseUrl);
        var ratio = DomainLinkRatio.Calculate(links, job.StartHost);

        // Only same-domain links are followed, and only while we're above the depth limit.
        var children = page.Depth < job.MaxDepth
            ? links.Where(u => DomainLinkRatio.IsSameDomain(u, job.StartHost)).Select(u => u.AbsoluteUri).ToList()
            : [];

        await crawl.SaveCrawledPageAsync(job.Id, new CrawledPage(
            page.Id, html.Status, ratio, links.Select(u => u.AbsoluteUri).ToList(), children, page.Depth + 1), ct);

        logger.LogInformation("Crawled {Url} (depth {Depth}): {LinkCount} links, ratio {Ratio:F2}, {ChildCount} same-domain",
            page.Url, page.Depth, links.Count, ratio, children.Count);
    }
}
