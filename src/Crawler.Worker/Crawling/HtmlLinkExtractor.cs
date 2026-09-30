using AngleSharp.Html.Parser;

namespace Crawler.Worker.Crawling;

public static class HtmlLinkExtractor
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// Raw href values of all &lt;a&gt; and &lt;area&gt; links, plus the URL relative links resolve against:
    /// the page URL, or the document's &lt;base href&gt; if it declares one.
    /// </summary>
    public static (Uri BaseUrl, IReadOnlyList<string> Hrefs) Extract(string html, Uri pageUrl)
    {
        using var document = Parser.ParseDocument(html);

        var baseHref = document.QuerySelector("base[href]")?.GetAttribute("href");
        var baseUrl = baseHref is not null && Uri.TryCreate(pageUrl, baseHref.Trim(), out var b) ? b : pageUrl;

        var hrefs = document.QuerySelectorAll("a[href], area[href]")
            .Select(e => e.GetAttribute("href")!)
            .ToList();

        return (baseUrl, hrefs);
    }
}
