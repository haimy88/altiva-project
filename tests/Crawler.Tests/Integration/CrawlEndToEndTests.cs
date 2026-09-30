using Crawler.Domain.Jobs;
using Crawler.Infrastructure.Persistence;
using Crawler.Worker.Crawling;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crawler.Tests.Integration;

/// <summary>
/// Runs the real crawl pipeline (CrawlJobProcessor → HttpPageFetcher → AngleSharp → normalizer → ratio → Postgres)
/// against local HTML fixtures. No RabbitMQ: the consumer just calls ProcessAsync, which is what we call here.
/// </summary>
[Trait("Category", "Integration")]
public class CrawlEndToEndTests(CrawlFixture fixture) : IClassFixture<CrawlFixture>
{
    private string Url(string path) => new Uri(fixture.SiteUrl, path).AbsoluteUri;

    private async Task<CrawlJob> CrawlAsync(int maxDepth = 2, int maxPages = 200, int parallelCopies = 1)
    {
        var jobs = new JobRepository(fixture.Db);
        var job = CrawlJob.CreateNew(fixture.SiteUrl, maxDepth, maxPages);
        await jobs.CreateAsync(job, default);

        // parallelCopies > 1 simulates the same message being delivered to several workers at once.
        var runs = Enumerable.Range(0, parallelCopies).Select(_ => NewProcessor().ProcessAsync(job.Id, default));
        await Task.WhenAll(runs);

        return (await jobs.GetAsync(job.Id, default))!;
    }

    private CrawlJobProcessor NewProcessor() => new(
        new JobRepository(fixture.Db),
        new CrawlRepository(fixture.Db),
        new HttpPageFetcher(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }),
        NullLogger<CrawlJobProcessor>.Instance);

    [Fact]
    public async Task Crawls_fixture_site_into_the_expected_tree()
    {
        var job = await CrawlAsync(maxDepth: 2);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.CompletedAt);

        var root = (await new JobRepository(fixture.Db).GetTreeAsync(job.Id, default))!;

        // Root: 6 distinct crawlable links (/, about, blog, external, missing, data.json). The nav/footer repeats,
        // the #fragment duplicate, #top, mailto: and javascript: are dropped. 5 of 6 are on-site.
        Assert.Equal(Url("/"), root.Url);
        Assert.Equal(PageStatus.Crawled, root.Status);
        Assert.Equal(6, root.OutgoingLinks.Count);
        Assert.Contains("https://external.example/", root.OutgoingLinks);
        Assert.Equal(5.0 / 6, root.DomainLinkRatio!.Value, precision: 10);

        // Depth 1: every on-site link of the root, in link order, with the right outcome.
        var children = root.Children.ToDictionary(c => c.Url);
        Assert.Equal(new[] { Url("/about.html"), Url("/blog/"), Url("/missing.html"), Url("/data.json") },
            root.Children.Select(c => c.Url));
        Assert.All(root.Children, c => Assert.Equal(1, c.Depth));

        var about = children[Url("/about.html")];
        Assert.Equal(PageStatus.Crawled, about.Status);
        Assert.Equal(0.5, about.DomainLinkRatio);  // nav (3) on-site; external a, other b, partner off-site
        Assert.Empty(about.Children);              // everything on-site was already known

        var missing = children[Url("/missing.html")];
        Assert.Equal(PageStatus.Failed, missing.Status);
        Assert.Equal(404, missing.HttpStatus);

        var data = children[Url("/data.json")];
        Assert.Equal(PageStatus.Skipped, data.Status);
        Assert.Contains("Not HTML", data.Error);

        // Depth 2: post1 found via a relative link on /blog/; "../about.html" was already known.
        var blog = children[Url("/blog/")];
        Assert.Equal(0.8, blog.DomainLinkRatio); // nav (3) + post1 on-site, partner off-site
        var post = Assert.Single(blog.Children);
        Assert.Equal(Url("/blog/post1.html"), post.Url);
        Assert.Equal(2, post.Depth);

        // Depth limit: post1's link to deep.html is recorded as an edge, but the page is never crawled.
        Assert.Contains(Url("/blog/deep.html"), post.OutgoingLinks);
        Assert.Empty(post.Children);

        await AssertRowCounts(job.Id, pages: 6, links: 22);
    }

    [Fact]
    public async Task Same_job_delivered_to_three_workers_at_once_creates_no_duplicate_rows()
    {
        var job = await CrawlAsync(maxDepth: 2, parallelCopies: 3);

        Assert.Equal(JobStatus.Completed, job.Status);
        await AssertRowCounts(job.Id, pages: 6, links: 22); // identical to a single run
    }

    [Fact]
    public async Task Crawling_an_already_completed_job_again_changes_nothing()
    {
        var job = await CrawlAsync(maxDepth: 2);

        await NewProcessor().ProcessAsync(job.Id, default); // redelivered after completion

        await AssertRowCounts(job.Id, pages: 6, links: 22);
    }

    [Fact]
    public async Task Max_depth_0_crawls_only_the_start_page()
    {
        var job = await CrawlAsync(maxDepth: 0);

        var root = (await new JobRepository(fixture.Db).GetTreeAsync(job.Id, default))!;
        Assert.Equal(6, root.OutgoingLinks.Count); // links still recorded
        Assert.Empty(root.Children);               // but not followed
        await AssertRowCounts(job.Id, pages: 1, links: 6);
    }

    [Fact]
    public async Task Max_pages_limit_is_never_exceeded()
    {
        var job = await CrawlAsync(maxDepth: 2, maxPages: 3);

        Assert.Equal(JobStatus.Completed, job.Status);
        await using var conn = await fixture.Db.OpenConnectionAsync();
        var pages = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM pages WHERE job_id = @id", new { id = job.Id });
        Assert.Equal(3, pages); // root + first 2 on-site links in page order
    }

    private async Task AssertRowCounts(Guid jobId, int pages, int links)
    {
        await using var conn = await fixture.Db.OpenConnectionAsync();
        var counts = await conn.QuerySingleAsync<(long Pages, long DistinctUrls, long Links, long DistinctLinks)>("""
            SELECT (SELECT count(*) FROM pages WHERE job_id = @jobId),
                   (SELECT count(DISTINCT url) FROM pages WHERE job_id = @jobId),
                   (SELECT count(*) FROM page_links l JOIN pages p ON p.id = l.page_id WHERE p.job_id = @jobId),
                   (SELECT count(DISTINCT (l.page_id, l.target_url)) FROM page_links l JOIN pages p ON p.id = l.page_id WHERE p.job_id = @jobId)
            """, new { jobId });

        Assert.Equal(pages, counts.Pages);
        Assert.Equal(pages, counts.DistinctUrls);
        Assert.Equal(links, counts.Links);
        Assert.Equal(links, counts.DistinctLinks);
    }
}
