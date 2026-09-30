namespace Crawler.Domain.Links;

/// <summary>
/// Turns a raw href found on a page into one canonical absolute URL, so the same page
/// is always represented by the same string (used for de-duplication and the tree).
/// </summary>
public static class UrlNormalizer
{
    /// <summary>
    /// Resolves <paramref name="href"/> against the page it was found on.
    /// Returns null for anything that isn't a crawlable http(s) link:
    /// empty hrefs, in-page anchors ("#top"), mailto:, tel:, javascript:, data:, etc.
    /// </summary>
    public static Uri? Normalize(string? href, Uri pageUrl)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;

        var trimmed = href.Trim();
        if (trimmed.StartsWith('#')) return null; // same-page anchor, not a link to another page

        if (!Uri.TryCreate(pageUrl, trimmed, out var resolved)) return null;
        if (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps) return null;

        // Uri already lowercases scheme/host, drops default ports and resolves "../".
        // We additionally drop the fragment: /about#team and /about are the same page.
        return new UriBuilder(resolved) { Fragment = string.Empty }.Uri;
    }

    /// <summary>Normalizes the start URL a user submitted. Must be an absolute http(s) URL.</summary>
    public static Uri? NormalizeStartUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var absolute) ? Normalize(absolute.AbsoluteUri, absolute) : null;

    /// <summary>All distinct crawlable links on a page, normalized, in first-seen order.</summary>
    public static IReadOnlyList<Uri> NormalizeAll(IEnumerable<string?> hrefs, Uri pageUrl) =>
        hrefs.Select(h => Normalize(h, pageUrl))
             .OfType<Uri>()
             .DistinctBy(u => u.AbsoluteUri)
             .ToList();
}
