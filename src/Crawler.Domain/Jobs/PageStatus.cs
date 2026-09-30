namespace Crawler.Domain.Jobs;

/// <summary>Pending = queued to crawl; Crawled = HTML fetched + links saved; Failed = fetch error; Skipped = not HTML.</summary>
public enum PageStatus { Pending, Crawled, Failed, Skipped }
