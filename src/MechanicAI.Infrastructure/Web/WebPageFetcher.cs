using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HtmlAgilityPack;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Infrastructure.Http;
using UglyToad.PdfPig;

namespace MechanicAI.Infrastructure.Web;

/// <summary>
/// Downloads a public web page (or PDF) and extracts its readable main text.
/// Protections: http/https only, public IP addresses only (no loopback/private/link-local —
/// prevents a malicious page or prompt from steering fetches into the shop network),
/// 3 MB size cap, 20 s timeout, redirects re-validated.
/// </summary>
public sealed class WebPageFetcher(IHttpClientFactory httpFactory) : IWebPageFetcher
{
    public const string HttpClientName = "web-fetch";
    private const string Service = "Web page";
    private const int MaxBytes = 3 * 1024 * 1024;
    private const int MaxTextChars = 40_000;
    private const int MaxRedirects = 4;

    private static readonly string[] RemoveSelectors =
    [
        "//script", "//style", "//noscript", "//nav", "//footer", "//header", "//aside", "//form", "//svg", "//iframe",
        "//*[@role='navigation']", "//*[@aria-hidden='true']", "//*[contains(@class,'cookie')]", "//*[contains(@class,'advert')]",
    ];

    public async Task<FetchedPage> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ExternalServiceException(Service, ErrorKind.Validation, "Only http and https pages can be fetched.");
        }

        var http = httpFactory.CreateClient(HttpClientName);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        var current = uri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            await EnsurePublicHostAsync(current, cts.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/pdf;q=0.9,text/plain;q=0.8");
            using var response = await SendNoThrowOnRedirectAsync(http, request, cts.Token);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ExternalServiceException(Service, response.StatusCode == HttpStatusCode.NotFound ? ErrorKind.NotFound : ErrorKind.Unavailable,
                    $"The page returned HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength is > MaxBytes)
            {
                throw new ExternalServiceException(Service, ErrorKind.InvalidResponse, "The page is too large to read.");
            }

            var bytes = await ReadLimitedAsync(response.Content, cts.Token);
            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "text/html";
            var finalUrl = current.ToString();

            if (mediaType == "application/pdf" || (bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F'))
            {
                return ExtractPdf(url, finalUrl, bytes);
            }

            if (!mediaType.Contains("html", StringComparison.Ordinal) && !mediaType.StartsWith("text/", StringComparison.Ordinal))
            {
                throw new ExternalServiceException(Service, ErrorKind.InvalidResponse, $"Unsupported content type ({mediaType}).");
            }

            var charset = response.Content.Headers.ContentType?.CharSet;
            var html = Decode(bytes, charset);
            return mediaType.Contains("html", StringComparison.Ordinal)
                ? ExtractHtml(url, finalUrl, html, (int)response.StatusCode)
                : new FetchedPage(url, finalUrl, null, Text.Truncate(html, MaxTextChars), null, mediaType, (int)response.StatusCode);
        }

        throw new ExternalServiceException(Service, ErrorKind.InvalidResponse, "Too many redirects.");
    }

    private static async Task<HttpResponseMessage> SendNoThrowOnRedirectAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ExternalServiceException(Service, ErrorKind.Timeout, "The page took too long to load.", ex);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            throw new ExternalServiceException(Service, ErrorKind.Timeout, "The page took too long to load.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalServiceException(Service, ErrorKind.Unavailable, "The page could not be reached.", ex);
        }
    }

    internal static async Task EnsurePublicHostAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
            }
            catch (SocketException ex)
            {
                throw new ExternalServiceException(Service, ErrorKind.Unavailable, "The page's address could not be resolved.", ex);
            }
        }

        if (addresses.Length == 0 || addresses.Any(IsPrivate))
        {
            throw new ExternalServiceException(Service, ErrorKind.Validation, "Pages on local or private network addresses are not fetched.");
        }
    }

    internal static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                   || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254)
                   || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                   || b[0] == 0
                   || b[0] >= 224;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast ||
               address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > MaxBytes) throw new ExternalServiceException(Service, ErrorKind.InvalidResponse, "The page is too large to read.");
            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(charset)) return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes);
        }
        catch (ArgumentException)
        {
        }

        return Encoding.UTF8.GetString(bytes);
    }

    internal static FetchedPage ExtractHtml(string requestedUrl, string finalUrl, string html, int status)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var title = Meta(doc, "og:title") ?? doc.DocumentNode.SelectSingleNode("//title")?.InnerText;
        var published = ParseDate(Meta(doc, "article:published_time") ?? Meta(doc, "article:modified_time") ?? Meta(doc, "og:updated_time")
                                  ?? Meta(doc, "date") ?? Meta(doc, "pubdate") ?? Meta(doc, "DC.date.issued")
                                  ?? doc.DocumentNode.SelectSingleNode("//*[@itemprop='datePublished']")?.GetAttributeValue("content", null!)
                                  ?? doc.DocumentNode.SelectSingleNode("//time[@datetime]")?.GetAttributeValue("datetime", null!));

        foreach (var selector in RemoveSelectors)
        {
            var nodes = doc.DocumentNode.SelectNodes(selector);
            if (nodes is null) continue;
            foreach (var node in nodes.ToList()) node.Remove();
        }

        var main = doc.DocumentNode.SelectSingleNode("//article")
                   ?? doc.DocumentNode.SelectSingleNode("//main")
                   ?? doc.DocumentNode.SelectSingleNode("//*[@role='main']")
                   ?? doc.DocumentNode.SelectSingleNode("//body")
                   ?? doc.DocumentNode;

        var sb = new StringBuilder();
        AppendText(main, sb);
        var text = NormalizeLines(sb.ToString());
        return new FetchedPage(requestedUrl, finalUrl, title is null ? null : WebUtility.HtmlDecode(Text.CollapseWhitespace(title)),
            Text.Truncate(text, MaxTextChars), published, "text/html", status);
    }

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "li", "tr", "br", "h1", "h2", "h3", "h4", "h5", "h6", "pre", "blockquote", "table", "ul", "ol", "dd", "dt",
    };

    private static void AppendText(HtmlNode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                sb.Append(WebUtility.HtmlDecode(child.InnerText));
            }
            else if (child.NodeType == HtmlNodeType.Element)
            {
                var block = BlockElements.Contains(child.Name);
                if (block) sb.Append('\n');
                if (child.Name is "h1" or "h2" or "h3" or "h4") sb.Append("## ");
                if (child.Name == "li") sb.Append("• ");
                AppendText(child, sb);
                if (block) sb.Append('\n');
                if (child.Name is "td" or "th") sb.Append(" | ");
            }
        }
    }

    private static string NormalizeLines(string text)
    {
        var lines = text.Split('\n')
            .Select(l => Text.CollapseWhitespace(l))
            .Where(l => l.Length > 0)
            .ToList();
        var sb = new StringBuilder();
        string? previous = null;
        foreach (var line in lines)
        {
            if (line == previous) continue;
            sb.AppendLine(line);
            previous = line;
        }

        return sb.ToString().Trim();
    }

    private static string? Meta(HtmlDocument doc, string name)
    {
        var node = doc.DocumentNode.SelectSingleNode($"//meta[@property='{name}']") ?? doc.DocumentNode.SelectSingleNode($"//meta[@name='{name}']");
        var content = node?.GetAttributeValue("content", string.Empty);
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : null;

    private static FetchedPage ExtractPdf(string requestedUrl, string finalUrl, byte[] bytes)
    {
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            foreach (var page in pdf.GetPages().Take(40))
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"[Page {page.Number}]"));
                sb.AppendLine(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page));
                if (sb.Length > MaxTextChars) break;
            }

            var title = pdf.Information.Title;
            return new FetchedPage(requestedUrl, finalUrl, string.IsNullOrWhiteSpace(title) ? null : title, Text.Truncate(sb.ToString(), MaxTextChars),
                null, "application/pdf", 200);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ExternalServiceException(Service, ErrorKind.InvalidResponse, "The PDF could not be read.", ex);
        }
    }
}
