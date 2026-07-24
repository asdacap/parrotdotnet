using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Parrot.Web;

internal sealed class WebFetcher(
    IWebAddressPolicy addressPolicy,
    int maxRedirects,
    int maxBodyBytes,
    TimeSpan timeout,
    string userAgent)
{
    public static WebFetcher Create(IWebAddressPolicy addressPolicy) =>
        new(addressPolicy, 5, 2 << 20, TimeSpan.FromSeconds(20), "parrot-coder-webfetch/1");

    public static Uri NormalizeAddress(string address)
    {
        var raw = address.Trim();

        if (!raw.Contains("://", StringComparison.Ordinal))
        {
            raw = "https://" + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || parsed.Host.Length == 0
            || parsed.UserInfo.Length != 0)
        {
            throw new WebFetchException("web fetch URL must be HTTP or HTTPS without user information");
        }

        var normalized = new UriBuilder(parsed) { Fragment = string.Empty };
        return normalized.Uri;
    }

    public async Task<WebFetchResult> Fetch(
        Uri address, HttpMethod method, CancellationToken cancellationToken)
    {
        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            throw new WebFetchException("web fetch supports only GET and HEAD");
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);

        try
        {
            return await FetchCore(address, method, timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebFetchException("web fetch timed out", failure);
        }
        catch (HttpRequestException failure)
        {
            throw new WebFetchException("web fetch request failed", failure);
        }
    }

    private SocketsHttpHandler Handler(ConcurrentDictionary<string, IPAddress[]> pins) =>
        new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = (context, token) => WebConnection.Connect(pins, context.DnsEndPoint, token),
            ConnectTimeout = timeout,
            PooledConnectionLifetime = TimeSpan.Zero,
            SslOptions = new SslClientAuthenticationOptions(),
            UseProxy = false,
        };

    private async Task<WebFetchResult> FetchCore(Uri address, HttpMethod method, CancellationToken cancellationToken)
    {
        var pins = new ConcurrentDictionary<string, IPAddress[]>(StringComparer.OrdinalIgnoreCase);
        using var handler = Handler(pins);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var current = address;

        for (var redirect = 0; ; redirect++)
        {
            await Pin(current, pins, cancellationToken).ConfigureAwait(false);
            var (result, next) = await FetchOnce(client, current, method, cancellationToken).ConfigureAwait(false);

            if (result is not null)
            {
                return result;
            }

            if (redirect >= maxRedirects)
            {
                throw new WebFetchException("web fetch followed too many redirects");
            }

            current = next ?? throw new WebFetchException("web fetch redirect did not provide an address");
        }
    }

    private async Task<(WebFetchResult? Result, Uri? Redirect)> FetchOnce(
        HttpClient client,
        Uri address,
        HttpMethod method,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, address);
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (WebFetchText.IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
        {
            return (null, WebFetchText.Redirect(address, location));
        }

        var contentType = WebFetchText.ContentType(response);
        var (text, truncated) = await ReadText(response, contentType, cancellationToken).ConfigureAwait(false);
        return (new(address, (int)response.StatusCode, contentType, text, truncated), null);
    }

    private async Task Pin(
        Uri address,
        ConcurrentDictionary<string, IPAddress[]> pins,
        CancellationToken cancellationToken)
    {
        if (address.Scheme is not ("http" or "https"))
        {
            throw new WebFetchException("web fetch redirect used a forbidden scheme");
        }

        var host = WebFetchText.CanonicalHost(address.DnsSafeHost);

        if (host.Length == 0 || !addressPolicy.AllowsHost(host))
        {
            throw new WebFetchException("web fetch local or empty host is forbidden");
        }

        if (pins.ContainsKey(host))
        {
            return;
        }

        IPAddress[] resolved;

        if (IPAddress.TryParse(host, out var literal))
        {
            resolved = [literal.IsIPv4MappedToIPv6 ? literal.MapToIPv4() : literal];
        }
        else
        {
            try
            {
                resolved = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException failure)
            {
                throw new WebFetchException($"web fetch could not resolve {host}", failure);
            }
        }

        var allowed = resolved
            .Select(item => item.IsIPv4MappedToIPv6 ? item.MapToIPv4() : item)
            .Where(addressPolicy.Allows)
            .Distinct()
            .OrderBy(item => item.AddressFamily)
            .ThenBy(item => item.ToString(), StringComparer.Ordinal)
            .ToArray();

        if (allowed.Length == 0)
        {
            throw new WebFetchException("web fetch host resolves only to forbidden addresses");
        }

        _ = pins.TryAdd(host, allowed);
    }

    private async Task<(string Text, bool Truncated)> ReadText(
        HttpResponseMessage response,
        string contentType,
        CancellationToken cancellationToken)
    {
        var encodings = response.Content.Headers.ContentEncoding;

        if (encodings.Count > 1
            || (encodings.Count == 1
            && !encodings.First().Equals("identity", StringComparison.OrdinalIgnoreCase)
            && !encodings.First().Equals("gzip", StringComparison.OrdinalIgnoreCase)))
        {
            throw new WebFetchException("web fetch response used an unsupported content encoding");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        if (encodings.Count == 1 && encodings.First().Equals("gzip", StringComparison.OrdinalIgnoreCase))
        {
            await using var decompressedStream = new GZipStream(responseStream, CompressionMode.Decompress);
            return await ReadBody(decompressedStream, contentType, cancellationToken).ConfigureAwait(false);
        }

        return await ReadBody(responseStream, contentType, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(string Text, bool Truncated)> ReadBody(
        Stream contentStream,
        string contentType,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[maxBodyBytes + 1];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await contentStream
                .ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            length += read;
        }

        var truncated = length > maxBodyBytes;
        var text = Encoding.UTF8.GetString(buffer, 0, Math.Min(length, maxBodyBytes));
        text = contentType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            ? HtmlText.Extract(text)
            : WebFetchText.StripControls(text);
        return (text, truncated);
    }
}
