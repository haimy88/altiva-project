using Crawler.Domain.Links;

namespace Crawler.Tests.Domain;

public class DomainLinkRatioTests
{
    private const string StartHost = "site.com";

    private static Uri[] Links(params string[] urls) => urls.Select(u => new Uri(u)).ToArray();

    [Fact]
    public void Zero_links_gives_zero() =>
        Assert.Equal(0, DomainLinkRatio.Calculate(Array.Empty<Uri>(), StartHost));

    [Fact]
    public void All_internal_gives_one() =>
        Assert.Equal(1, DomainLinkRatio.Calculate(Links("https://site.com/a", "https://site.com/b"), StartHost));

    [Fact]
    public void All_external_gives_zero() =>
        Assert.Equal(0, DomainLinkRatio.Calculate(Links("https://other.com/a", "https://x.org/"), StartHost));

    [Fact]
    public void Mixed_links_give_fraction()
    {
        var links = Links("https://site.com/a", "https://site.com/b", "https://site.com/c", "https://other.com/");
        Assert.Equal(0.75, DomainLinkRatio.Calculate(links, StartHost));
    }

    [Fact]
    public void Host_match_is_case_insensitive() =>
        Assert.Equal(1, DomainLinkRatio.Calculate(Links("https://site.com/a"), "SITE.COM"));

    [Fact]
    public void Http_and_https_on_same_host_both_count_as_internal() =>
        Assert.Equal(1, DomainLinkRatio.Calculate(Links("http://site.com/a", "https://site.com/b"), StartHost));

    [Theory]
    [InlineData("https://www.site.com/a")]  // subdomain is a different host
    [InlineData("https://blog.site.com/a")]
    [InlineData("https://site.com.evil.com/a")]
    public void Other_hosts_are_external(string url) =>
        Assert.Equal(0, DomainLinkRatio.Calculate(Links(url), StartHost));
}
