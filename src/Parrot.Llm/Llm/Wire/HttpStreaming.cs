using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Parrot.Llm.Wire;

// The shared HTTP/SSE machinery every provider reuses: endpoint and header
// validation, a header-timeout-bounded streaming POST, a bounded non-streaming
// GET, and structured error extraction. Port of Go's provider/http.go, minus
// the secret-redaction layer (deliberately omitted for this port).
internal static class HttpStreaming
{
    public const int MaxErrorBytes = 64 << 10;
    public const int MaxEventBytes = 4 << 20;
    public const long MaxStreamBytes = 64L << 20;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ModelsRefreshTimeout = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "cookie",
        "host",
        "proxy-authorization",
    };

    public static Uri EndpointUrl(
        string baseUrl,
        string endpoint,
        bool allowInsecureLocalhost,
        bool allowInsecureRemote)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed))
        {
            throw new ProviderHttpException($"provider: invalid base URL: {baseUrl}");
        }

        if (string.IsNullOrEmpty(parsed.Host) || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment) || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            throw new ProviderHttpException("provider: base URL must contain only a scheme, host, and path");
        }

        switch (parsed.Scheme)
        {
            case "https":
                break;

            case "http" when allowInsecureRemote || (allowInsecureLocalhost && IsLoopbackHost(parsed.Host)):
                break;

            case "http":
                throw new ProviderHttpException(
                    "provider: HTTP requires allow_insecure_remote, or allow_insecure_localhost for a loopback host");

            default:
                throw new ProviderHttpException("provider: base URL must use HTTPS");
        }

        var path = parsed.AbsolutePath.TrimEnd('/') + "/" + endpoint;
        return new UriBuilder(parsed) { Path = path }.Uri;
    }

    public static IReadOnlyDictionary<string, string> ValidateHeaders(IReadOnlyDictionary<string, string> headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in headers)
        {
            if (name.Trim() != name || !ValidHeaderName(name))
            {
                throw new ProviderHttpException($"provider: invalid configured header name \"{name}\"");
            }

            if (ForbiddenHeaders.Contains(name))
            {
                throw new ProviderHttpException($"provider: configured header \"{name}\" is not allowed");
            }

            if (value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
            {
                throw new ProviderHttpException($"provider: configured header \"{name}\" has an invalid value");
            }

            result[name] = value;
        }

        return result;
    }

    public static async Task<Dictionary<string, string>> ResolveBearerHeaders(
        IApiKeySource apiKeySource,
        string providerId,
        CancellationToken cancellationToken)
    {
        var apiKey = await apiKeySource.ApiKey(cancellationToken).ConfigureAwait(false);

        if (apiKey.Length == 0)
        {
            throw new LLMProviderException($"provider: \"{providerId}\" has no API key");
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + apiKey,
        };
    }

    // Opens a streaming POST. The header timeout bounds only time-to-headers;
    // each body read has an idle timeout, while active streams have no total deadline.
    public static async Task<StreamingHttpResponse> OpenStream(
        HttpClient client,
        Uri endpoint,
        byte[] body,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan headerTimeout,
        TimeSpan streamIdleTimeout,
        int maximumRequestBytes,
        IProviderAttemptDiagnostics? attempt,
        CancellationToken cancellationToken)
    {
        if (streamIdleTimeout < TimeSpan.Zero)
        {
            throw new LLMProviderException("provider: stream idle timeout cannot be negative");
        }

        if (body.Length > maximumRequestBytes)
        {
            throw new ProviderHttpException($"provider: request exceeds {maximumRequestBytes} bytes");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };

        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        _ = message.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

        foreach (var (name, value) in headers)
        {
            _ = message.Headers.TryAddWithoutValidation(name, value);
        }

        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (headerTimeout > TimeSpan.Zero)
        {
            headerCts.CancelAfter(headerTimeout);
        }

        HttpResponseMessage response;

        try
        {
            response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, headerCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && headerTimeout > TimeSpan.Zero)
        {
            throw new HeaderTimeoutException(headerTimeout);
        }

        attempt?.MarkResponseObtained();

        // The request message is disposed by the using; the response body stream
        // outlives it and is owned by the returned BoundedStream on success.
        var transferred = false;

        try
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await ParseError(response, attempt, cancellationToken).ConfigureAwait(false);
            }

            var raw = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var responseHeaders = response.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(", ", header.Value),
                StringComparer.OrdinalIgnoreCase);
            transferred = true;
            return new StreamingHttpResponse(raw, streamIdleTimeout, MaxStreamBytes, response, responseHeaders, attempt);
        }
        finally
        {
            if (!transferred)
            {
                response.Dispose();
            }
        }
    }

    public static async IAsyncEnumerable<LLMEvent> StreamEvents(
        HttpClient client,
        Uri endpoint,
        byte[] body,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan headerTimeout,
        TimeSpan streamIdleTimeout,
        int maximumRequestBytes,
        LLMRequest request,
        Action<string> captureTurnState,
        Func<Stream, int, CancellationToken, IAsyncEnumerable<LLMEvent>> parse,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (body.Length > maximumRequestBytes)
        {
            throw new ProviderHttpException($"provider: request exceeds {maximumRequestBytes} bytes");
        }

        var events = Send(null, cancellationToken);
        if (request.Diagnostics is { } diagnostics)
        {
            events = diagnostics.Trace(Send, "http_sse", cancellationToken);
        }

        await foreach (var published in events.ConfigureAwait(false))
        {
            yield return published;
        }

        async IAsyncEnumerable<LLMEvent> Send(IProviderAttemptDiagnostics? attempt, [EnumeratorCancellation] CancellationToken sendCancellationToken)
        {
            attempt?.RecordRequestBytes(body.Length);
            request.Diagnostics?.DumpRequest(body);
            yield return LLMEvent.HttpRequestStarted();
            var response = await OpenStream(client, endpoint, body, headers, headerTimeout, streamIdleTimeout, maximumRequestBytes, attempt, sendCancellationToken)
                .ConfigureAwait(false);
            CaptureTurnState(response.Headers, captureTurnState);

            await using (response.ConfigureAwait(false))
            {
                yield return LLMEvent.HttpResponseHeadersReceived();
                await foreach (var published in parse(response.Content, MaxEventBytes, sendCancellationToken).ConfigureAwait(false))
                {
                    yield return published;
                }
            }
        }
    }

    // A bounded non-streaming GET, used for model catalogues and usage.
    public static async Task<string> Get(
        HttpClient client,
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan timeout,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (timeout > TimeSpan.Zero)
        {
            timeoutCts.CancelAfter(timeout);
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        _ = message.Headers.TryAddWithoutValidation("Accept", "application/json");

        foreach (var (name, value) in headers)
        {
            _ = message.Headers.TryAddWithoutValidation(name, value);
        }

        try
        {
            using var response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw await ParseError(response, null, timeoutCts.Token).ConfigureAwait(false);
            }

            var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await ReadBounded(stream, maxBytes, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderHttpException("provider: request timed out");
        }
    }

    private static void CaptureTurnState(
        IReadOnlyDictionary<string, string> headers,
        Action<string> captureTurnState)
    {
        foreach (var header in headers)
        {
            if (header.Key.Equals("x-codex-turn-state", StringComparison.OrdinalIgnoreCase))
            {
                captureTurnState(header.Value);
                break;
            }
        }
    }

    private static async Task<string> ReadBounded(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[maxBytes + 1];
        var total = await stream
            .ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);

        if (total > maxBytes)
        {
            throw new ProviderHttpException($"provider: response exceeds {maxBytes} bytes");
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static async Task<ProviderHttpException> ParseError(
        HttpResponseMessage response,
        IProviderAttemptDiagnostics? attempt,
        CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        string body;

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                body = await ReadErrorBody(stream, attempt, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (failure is not ProviderHttpException && !cancellationToken.IsCancellationRequested)
        {
            return new ProviderHttpException(status, string.Empty, string.Empty, "unable to read provider error");
        }

        var (type, code, detail) = ExtractError(body);

        if (detail.Length == 0)
        {
            detail = Sanitize(body, 1024);
        }

        if (detail.Length == 0)
        {
            detail = response.ReasonPhrase ?? string.Empty;
        }

        return new ProviderHttpException(
            status,
            Sanitize(type, 128),
            Sanitize(code, 128),
            detail,
            ProviderErrors.BoundResponseBody(body));
    }

    private static async Task<string> ReadErrorBody(
        Stream stream,
        IProviderAttemptDiagnostics? attempt,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxErrorBytes + 4];
        var total = await stream
            .ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        attempt?.RecordResponseBytes(total);
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static (string Type, string Code, string Detail) ExtractError(string body)
    {
        if (body.Length == 0)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return (string.Empty, string.Empty, string.Empty);
            }

            var scope = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;

            var type = JsonRead.String(scope, "type");
            var code = ReadScalar(scope, "code");
            var message = JsonRead.String(scope, "message");
            return (type, code, Sanitize(message, 1024));
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty, string.Empty);
        }
    }

    private static string ReadScalar(JsonElement scope, string name)
    {
        if (!scope.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }

    // Strips control characters (keeping tab), trims, and truncates. No secret
    // redaction — that layer is out of scope for this port.
    private static string Sanitize(string value, int limit)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (char.IsControl(character) && character != '\t')
            {
                continue;
            }

            _ = builder.Append(character);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length <= limit ? cleaned : cleaned[..limit];
    }

    private static bool ValidHeaderName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        foreach (var character in name)
        {
            var allowed = character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                || "!#$%&'*+-.^_`|~".Contains(character, StringComparison.Ordinal);

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }
}
