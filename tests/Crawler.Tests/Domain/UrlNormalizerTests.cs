using Crawler.Domain.Links;

namespace Crawler.Tests.Domain;

public class UrlNormalizerTests
{
    private static readonly Uri Page = new("https://site.com/blog/post");

    [Theory]
    [InlineData("/about", "https://site.com/about")]                 // root-relative
    [InlineData("other", "https://site.com/blog/other")]             // relative to current folder
    [InlineData("../about", "https://site.com/about")]               // parent folder
    [InlineData("./x", "https://site.com/blog/x")]                   // current folder
    [InlineData("//cdn.site.com/a.js", "https://cdn.site.com/a.js")] // protocol-relative inherits https
    [InlineData("?page=2", "https://site.com/blog/post?page=2")]     // query-only
    public void Resolves_relative_links_against_the_page(string href, string expected) =>
        Assert.Equal(expected, UrlNormalizer.Normalize(href, Page)!.AbsoluteUri);

    [Theory]
    [InlineData("/about#team", "https://site.com/about")]
    [InlineData("https://site.com/about#contact", "https://site.com/about")]
    [InlineData("/about?x=1#y", "https://site.com/about?x=1")]
    public void Strips_fragment(string href, string expected) =>
        Assert.Equal(expected, UrlNormalizer.Normalize(href, Page)!.AbsoluteUri);

    [Theory]
    [InlineData("HTTPS://SITE.COM/about", "https://site.com/about")]     // scheme + host lowercased
    [InlineData("https://site.com:443/about", "https://site.com/about")] // default port dropped
    [InlineData("http://site.com:80/", "http://site.com/")]
    [InlineData("https://site.com", "https://site.com/")]                // empty path becomes "/"
    [InlineData("  /about  ", "https://site.com/about")]                 // whitespace trimmed
    public void Produces_one_canonical_form(string href, string expected) =>
        Assert.Equal(expected, UrlNormalizer.Normalize(href, Page)!.AbsoluteUri);

    [Fact]
    public void Keeps_path_case_and_non_default_port()
    {
        Assert.Equal("https://site.com/About", UrlNormalizer.Normalize("/About", Page)!.AbsoluteUri);
        Assert.Equal("https://site.com:8443/", UrlNormalizer.Normalize("https://site.com:8443/", Page)!.AbsoluteUri);
    }

    [Theory]
    [InlineData("mailto:a@site.com")]
    [InlineData("tel:+123456")]
    [InlineData("javascript:void(0)")]
    [InlineData("ftp://site.com/file")]
    [InlineData("data:text/html,hi")]
    [InlineData("#top")]   // in-page anchor: not a link to another page
    [InlineData("#")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ignores_non_crawlable_links(string? href) =>
        Assert.Null(UrlNormalizer.Normalize(href, Page));

    [Fact]
    public void Ignores_urls_longer_than_the_limit()
    {
        var tooLong = "/" + new string('a', UrlNormalizer.MaxUrlLength);
        Assert.Null(UrlNormalizer.Normalize(tooLong, Page));
        Assert.Null(UrlNormalizer.NormalizeStartUrl("https://site.com" + tooLong));
    }

    [Fact]
    public void NormalizeAll_removes_duplicates_that_differ_only_in_form()
    {
        var hrefs = new[] { "/about", "/about#team", "https://SITE.com/about", "mailto:x@y.z", "/contact" };

        var result = UrlNormalizer.NormalizeAll(hrefs, Page).Select(u => u.AbsoluteUri);

        Assert.Equal(new[] { "https://site.com/about", "https://site.com/contact" }, result);
    }

    [Theory]
    [InlineData("https://site.com/start#x", "https://site.com/start")]
    [InlineData("  http://Site.com  ", "http://site.com/")]
    public void NormalizeStartUrl_accepts_absolute_http_urls(string input, string expected) =>
        Assert.Equal(expected, UrlNormalizer.NormalizeStartUrl(input)!.AbsoluteUri);

    [Theory]
    [InlineData("site.com")]        // no scheme
    [InlineData("/about")]          // relative
    [InlineData("ftp://site.com")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeStartUrl_rejects_invalid_input(string? input) =>
        Assert.Null(UrlNormalizer.NormalizeStartUrl(input));
}
