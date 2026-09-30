using System.Net.Http.Headers;
using System.Text;
using Polly;

namespace Crawler.Worker.Crawling;

public abstract record FetchResult(int? HttpStatus)
{
    /// <summary>An HTML page. FinalUrl differs from the requested URL if we were redirected.</summary>
    public sealed record Html(int Status, Uri FinalUrl, string Content) : FetchResult(Status);

    /// <summary>Fetched fine but not something we crawl (not HTML, redirected off-site, ...).</summary>
    public sealed record Skipped(int? Status, string Reason) : FetchResult(Status);

    /// <summary>Error status or network failure after the HTTP retries were exhausted.</summary>
    public sealed record Failed(int? Status, string Reason) : FetchResult(Status);
}

public interface IPageFetcher
{
    Task<FetchResult> FetchAsync(Uri url, string startHost, CancellationToken ct);
}

/// <summary>
/// Downloads one page. Timeouts and retries for transient HTTP failures (5xx, 408, 429, network errors)
/// come from the resilience handler configured in Program.cs, so this class only interprets the outcome.
/// </summary>
public sealed class HttpPageFetcher(HttpClient http) : IPageFetcher
{
    public const int MaxHtmlBytes = 5 * 1024 * 1024; // don't buffer giant pages

    /// <summary>The resilience timeouts stop once headers arrive, so reading the body gets its own limit.</summary>
    public static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(15);

    static HttpPageFetcher() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // windows-1252, Shift_JIS, ...

    private static readonly string[] HtmlMediaTypes = ["text/html", "application/xhtml+xml"];

    public async Task<FetchResult> FetchAsync(Uri url, string startHost, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;

            // A 3xx here is a redirect the handler refused to follow, e.g. HTTPS → HTTP (.NET blocks insecure downgrades).
            if (status is >= 300 and < 400)
                return new FetchResult.Skipped(status, $"Redirect not followed → {response.Headers.Location}");

            if (!response.IsSuccessStatusCode)
                return new FetchResult.Failed(status, $"HTTP {status} {response.ReasonPhrase}");

            var finalUrl = response.RequestMessage?.RequestUri ?? url;
            if (!string.Equals(finalUrl.Host, startHost, StringComparison.OrdinalIgnoreCase))
                return new FetchResult.Skipped(status, $"Redirected off-site to {finalUrl.Host}");

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !HtmlMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
                return new FetchResult.Skipped(status, $"Not HTML ({mediaType ?? "no content type"})");

            if (response.Content.Headers.ContentLength > MaxHtmlBytes)
                return new FetchResult.Skipped(status, "Page too large");

            using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bodyTimeout.CancelAfter(BodyReadTimeout);
            var html = await ReadLimitedAsync(response.Content, bodyTimeout.Token);
            return new FetchResult.Html(status, finalUrl, html);
        }
        catch (Exception ex) when (IsPageLevelFailure(ex, ct))
        {
            // Whatever went wrong with this one page, it's this page's problem: the job carries on.
            return new FetchResult.Failed(null, ex.Message);
        }
    }

    /// <summary>
    /// Network errors, timeouts (ours or Polly's TimeoutRejectedException), an open circuit (BrokenCircuitException)
    /// and connection resets mid-body. Only a real shutdown (ct canceled) is allowed to escape.
    /// </summary>
    private static bool IsPageLevelFailure(Exception ex, CancellationToken ct) => ex switch
    {
        HttpRequestException or IOException or TimeoutException or ExecutionRejectedException => true,
        OperationCanceledException => !ct.IsCancellationRequested,
        _ => false,
    };

    /// <summary>Reads at most MaxHtmlBytes (Content-Length can be missing or wrong).</summary>
    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxHtmlBytes];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct)) > 0)
            total += read;

        return GetEncoding(content.Headers.ContentType).GetString(buffer, 0, total);
    }

    private static Encoding GetEncoding(MediaTypeHeaderValue? contentType)
    {
        try { return contentType?.CharSet is { } cs ? Encoding.GetEncoding(cs.Trim('"')) : Encoding.UTF8; }
        catch (ArgumentException) { return Encoding.UTF8; }
    }
}
