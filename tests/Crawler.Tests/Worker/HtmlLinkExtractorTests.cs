using Crawler.Domain.Links;
using Crawler.Worker.Crawling;

namespace Crawler.Tests.Worker;

public class HtmlLinkExtractorTests
{
    private static readonly Uri Page = new("https://site.com/blog/post");

    [Fact]
    public void Extracts_anchor_and_area_hrefs_in_document_order()
    {
        const string html = """
            <a href="/a">A</a>
            <map><area href="/b"></map>
            <a>no href</a>
            <link href="/style.css"><img src="/x.png">
            <a href="mailto:x@y.z">mail</a>
            """;

        var (_, hrefs) = HtmlLinkExtractor.Extract(html, Page);

        Assert.Equal(new[] { "/a", "/b", "mailto:x@y.z" }, hrefs);
    }

    [Fact]
    public void Uses_page_url_as_base_by_default()
    {
        var (baseUrl, _) = HtmlLinkExtractor.Extract("<a href='x'>x</a>", Page);
        Assert.Equal(Page, baseUrl);
    }

    [Fact]
    public void Respects_base_href()
    {
        var (baseUrl, _) = HtmlLinkExtractor.Extract("<head><base href='/docs/'></head><a href='x'>x</a>", Page);
        Assert.Equal("https://site.com/docs/", baseUrl.AbsoluteUri);
    }

    [Fact]
    public void Tolerates_broken_html()
    {
        // Parsed like a browser would (HTML5 rules may repeat an unclosed <a>); duplicates are removed later by NormalizeAll.
        var (_, hrefs) = HtmlLinkExtractor.Extract("<div><a href='/ok'>unclosed <p><a href=/also-ok>", Page);
        Assert.Equal(new[] { "/ok", "/also-ok" }, hrefs.Distinct());
    }

    [Fact]
    public void Ignores_links_inside_comments_scripts_and_styles()
    {
        const string html = """
            <!-- <a href="/commented-out">x</a> -->
            <script>document.write('<a href="/from-script">x</a>');</script>
            <style>a[href="/from-css"] { color: red }</style>
            <a href="/real">real</a>
            """;

        var (_, hrefs) = HtmlLinkExtractor.Extract(html, Page);

        Assert.Equal(new[] { "/real" }, hrefs);
    }

    [Fact]
    public void Decodes_html_entities_in_hrefs()
    {
        var (_, hrefs) = HtmlLinkExtractor.Extract("<a href=\"/search?q=a&amp;page=2\">x</a>", Page);
        Assert.Equal(new[] { "/search?q=a&page=2" }, hrefs);
    }

    [Fact]
    public void Handles_uppercase_tags_and_unquoted_or_single_quoted_attributes()
    {
        const string html = "<A HREF=\"/upper\">x</A><a href=/unquoted>y</a><a href='/single'>z</a>";

        var (_, hrefs) = HtmlLinkExtractor.Extract(html, Page);

        Assert.Equal(new[] { "/upper", "/unquoted", "/single" }, hrefs);
    }

    [Fact]
    public void Keeps_empty_and_whitespace_hrefs_for_the_normalizer_to_drop()
    {
        var (_, hrefs) = HtmlLinkExtractor.Extract("<a href=\"\">a</a><a href=\"  \">b</a>", Page);

        Assert.Equal(2, hrefs.Count);
        Assert.Empty(UrlNormalizer.NormalizeAll(hrefs, Page));
    }

    [Fact]
    public void Empty_or_linkless_document_gives_no_links()
    {
        Assert.Empty(HtmlLinkExtractor.Extract("", Page).Hrefs);
        Assert.Empty(HtmlLinkExtractor.Extract("<html><body><p>No links here</p></body></html>", Page).Hrefs);
    }

    [Theory]
    [InlineData("<base href='https://cdn.other.com/assets/'>", "https://cdn.other.com/assets/")] // absolute base
    [InlineData("<base href='../'>", "https://site.com/")]                                        // relative base, resolved against the page
    [InlineData("<base target='_blank'>", "https://site.com/blog/post")]                         // base without href is ignored
    public void Base_href_variants(string baseTag, string expectedBase)
    {
        var (baseUrl, _) = HtmlLinkExtractor.Extract($"<head>{baseTag}</head><a href='x'>x</a>", Page);
        Assert.Equal(expectedBase, baseUrl.AbsoluteUri);
    }

    [Fact]
    public void Only_the_first_base_href_counts()
    {
        var (baseUrl, _) = HtmlLinkExtractor.Extract("<base href='/first/'><base href='/second/'>", Page);
        Assert.Equal("https://site.com/first/", baseUrl.AbsoluteUri);
    }

    [Fact]
    public void Realistic_page_end_to_end_links_and_ratio()
    {
        // A typical page: nav, content, footer. Parsed, normalized, then the ratio computed, as the worker does.
        const string html = """
            <!doctype html>
            <html><head><title>Blog post</title><link rel="stylesheet" href="/site.css"></head>
            <body>
              <nav><a href="/">Home</a> <a href="/about#team">About</a> <a href="https://SITE.com/about">About again</a></nav>
              <article>
                <p>Read <a href="../docs/intro">the docs</a> or <a href="https://github.com/org/repo">GitHub</a>.</p>
                <a href="#comments">Jump to comments</a>
                <a href="mailto:hi@site.com">Email</a> <a href="tel:+1555">Call</a> <a href="javascript:void(0)">JS</a>
              </article>
              <footer><a href="https://twitter.com/site">Twitter</a> <a href="/">Home again</a></footer>
            </body></html>
            """;

        var (baseUrl, hrefs) = HtmlLinkExtractor.Extract(html, Page);
        var links = UrlNormalizer.NormalizeAll(hrefs, baseUrl).Select(u => u.AbsoluteUri).ToList();

        Assert.Equal(new[]
        {
            "https://site.com/",
            "https://site.com/about",
            "https://site.com/docs/intro",
            "https://github.com/org/repo",
            "https://twitter.com/site",
        }, links);

        // 3 of the 5 distinct links stay on site.com.
        Assert.Equal(0.6, DomainLinkRatio.Calculate(links.Select(u => new Uri(u)).ToList(), "site.com"));
    }
}
