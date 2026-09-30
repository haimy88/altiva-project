namespace Crawler.Domain.Links;

public static class DomainLinkRatio
{
    /// <summary>
    /// (# outgoing links on the starting domain) / (total # outgoing links).
    /// "Starting domain" = exact host of the job's start URL (case-insensitive).
    /// Returns 0 when the page has no outgoing links.
    /// Expects links already normalized (non-http(s) schemes removed).
    /// </summary>
    public static double Calculate(IReadOnlyCollection<Uri> outgoingLinks, string startHost)
    {
        if (outgoingLinks.Count == 0) return 0;

        var internalCount = outgoingLinks.Count(u => IsSameDomain(u, startHost));
        return (double)internalCount / outgoingLinks.Count;
    }

    public static bool IsSameDomain(Uri url, string startHost) =>
        string.Equals(url.Host, startHost, StringComparison.OrdinalIgnoreCase);
}
