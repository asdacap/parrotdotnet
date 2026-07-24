using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Parrot.Auth;

// OpenAI (ChatGPT/Codex) OAuth: a PKCE authorization-code browser flow with a
// loopback callback, a headless device-code fallback, and token
// exchange/refresh. Port of Go's auth.OpenAI and auth.device.
internal sealed class OpenAiOAuthClient(HttpClient client, IBrowserOpener browser, OpenAiOAuthOptions options)
    : IOAuthClient
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public const string DefaultIssuer = "https://auth.openai.com";
    public const string CallbackUrl = "http://localhost:1455/auth/callback";

    private const int CallbackPort = 1455;

    // ExtractAccountId reads unverified JWT claims for request routing only. It
    // must never be used to authenticate or authorize a user.
    public static string ExtractAccountId(string token)
    {
        var parts = token.Split('.');

        if (parts.Length != 3)
        {
            return string.Empty;
        }

        byte[] payload;

        try
        {
            payload = Base64Url.DecodeFromChars(parts[1]);
        }
        catch (FormatException)
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var direct = ReadString(root, "chatgpt_account_id");

            if (direct.Length > 0)
            {
                return direct;
            }

            if (root.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object)
            {
                var nested = ReadString(auth, "chatgpt_account_id");

                if (nested.Length > 0)
                {
                    return nested;
                }
            }

            if (root.TryGetProperty("organizations", out var organizations)
                && organizations.ValueKind == JsonValueKind.Array)
            {
                foreach (var organization in organizations.EnumerateArray())
                {
                    var id = ReadString(organization, "id");

                    if (id.Length > 0)
                    {
                        return id;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        return string.Empty;
    }

    public DateTimeOffset Now() => options.Clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public string AuthorizationUrl(string redirect, string challenge, string state)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirect,
            ["scope"] = "openid profile email offline_access",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["state"] = state,
            ["originator"] = "opencode",
        };

        var encoded = string.Join('&', query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        return $"{Issuer()}/oauth/authorize?{encoded}";
    }

    // The PKCE browser flow. Always frees the loopback listener.
    public async Task<OAuthCredential> BrowserLogin(CancellationToken cancellationToken)
    {
        var verifier = RandomUrlString(32);
        var state = RandomUrlString(32);
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        var authorizationUrl = AuthorizationUrl(CallbackUrl, challenge, state);

        using var listener = new TcpListener(IPAddress.Loopback, CallbackPort);

        try
        {
            listener.Start();
        }
        catch (SocketException failure)
        {
            throw new AuthException("auth: start OAuth callback listener", failure);
        }

        using var loginCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loginCts.CancelAfter(options.LoginTimeout);

        try
        {
            await browser.Open(authorizationUrl, loginCts.Token).ConfigureAwait(false);
            var code = await AwaitCallback(listener, state, loginCts.Token).ConfigureAwait(false);
            return await Exchange(code, CallbackUrl, verifier, loginCts.Token).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task<OAuthCredential> Refresh(OAuthCredential current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken.Value,
            ["client_id"] = ClientId,
        };

        var tokens = await TokenRequest(form, cancellationToken).ConfigureAwait(false);
        return BuildCredential(tokens, current.AccountId);
    }

    public async Task<DeviceAuthorization> StartDeviceAuthorization(CancellationToken cancellationToken)
    {
        using var content = new StringContent($"{{\"client_id\":\"{ClientId}\"}}", Encoding.UTF8, "application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Issuer()}/api/accounts/deviceauth/usercode")
        {
            Content = content,
        };

        using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new AuthException($"auth: device authorization returned HTTP {(int)response.StatusCode}");
        }

        using var document = await ReadJson(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var deviceAuthId = ReadString(root, "device_auth_id");
        var userCode = ReadString(root, "user_code");

        if (deviceAuthId.Length == 0 || userCode.Length == 0)
        {
            throw new AuthException("auth: invalid device authorization response");
        }

        var interval = ParseInterval(root);

        if (interval < TimeSpan.FromSeconds(1))
        {
            interval = TimeSpan.FromSeconds(5);
        }

        var expires = TimeSpan.FromSeconds(ReadLong(root, "expires_in"));

        if (expires <= TimeSpan.Zero || expires > options.LoginTimeout)
        {
            expires = options.LoginTimeout;
        }

        return new DeviceAuthorization
        {
            DeviceAuthId = new Secret(deviceAuthId),
            UserCode = new Secret(userCode),
            VerificationUrl = $"{Issuer()}/codex/device",
            Interval = interval,
            ExpiresAt = Now().Add(expires),
        };
    }

    public async Task<OAuthCredential> AwaitDeviceAuthorization(
        DeviceAuthorization device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        var deadline = device.ExpiresAt;

        if (deadline == default || deadline > Now().Add(options.LoginTimeout))
        {
            deadline = Now().Add(options.LoginTimeout);
        }

        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = deadline - Now();

        if (remaining > TimeSpan.Zero)
        {
            pollCts.CancelAfter(remaining);
        }

        while (true)
        {
            var credential = await PollDevice(device, pollCts.Token).ConfigureAwait(false);

            if (credential is not null)
            {
                return credential;
            }

            await Task.Delay(device.Interval + options.PollingSafetyMargin, pollCts.Token).ConfigureAwait(false);
        }
    }

    private static async Task<string> AwaitCallback(TcpListener listener, string state, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var connection = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var stream = connection.GetStream();
            await using (stream.ConfigureAwait(false))
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                var target = RequestTarget(requestLine);

                if (!target.StartsWith("/auth/callback", StringComparison.Ordinal))
                {
                    await WriteResponse(stream, "404 Not Found", "Not found.", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var query = ParseQuery(target);
                string? error = null;

                if (query.ContainsKey("error"))
                {
                    error = "auth: authorization rejected";
                }
                else if (!query.TryGetValue("state", out var returned) || returned != state)
                {
                    error = "auth: invalid OAuth state";
                }
                else if (!query.TryGetValue("code", out var code) || code.Length == 0)
                {
                    error = "auth: missing authorization code";
                }

                if (error is not null)
                {
                    await WriteResponse(stream, "400 Bad Request", "Authorization failed. You may close this window.", cancellationToken)
                        .ConfigureAwait(false);
                    throw new AuthException(error);
                }

                await WriteResponse(stream, "200 OK", "Authorization complete. You may close this window.", cancellationToken)
                    .ConfigureAwait(false);
                return query["code"];
            }
        }
    }

    private static string RequestTarget(string? requestLine)
    {
        if (string.IsNullOrEmpty(requestLine))
        {
            return string.Empty;
        }

        var parts = requestLine.Split(' ');
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }

    private static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var mark = target.IndexOf('?', StringComparison.Ordinal);

        if (mark < 0)
        {
            return result;
        }

        foreach (var pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }

        return result;
    }

    private static async Task WriteResponse(
        Stream stream, string status, string body, CancellationToken cancellationToken)
    {
        var length = Encoding.UTF8.GetByteCount(body);
        var response = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {length}\r\nConnection: close\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(response), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[(1 << 20) + 1];
            var total = 0;

            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > 1 << 20)
            {
                throw new AuthException("auth: JSON response exceeds byte limit");
            }

            try
            {
                return JsonDocument.Parse(buffer.AsMemory(0, total));
            }
            catch (JsonException failure)
            {
                throw new AuthException("auth: invalid token response", failure);
            }
        }
    }

    private static TimeSpan ParseInterval(JsonElement root)
    {
        if (!root.TryGetProperty("interval", out var value))
        {
            return TimeSpan.Zero;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => TimeSpan.FromSeconds(value.GetInt64()),
            JsonValueKind.String => long.TryParse(value.GetString(), out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero,
            _ => TimeSpan.Zero,
        };
    }

    private static string RandomUrlString(int size) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(size));

    private static string ReadString(JsonElement scope, string name) =>
        scope.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadLong(JsonElement scope, string name) =>
        scope.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    private async Task<OAuthCredential> Exchange(
        string code, string redirect, string verifier, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
        };

        var tokens = await TokenRequest(form, cancellationToken).ConfigureAwait(false);
        return BuildCredential(tokens, string.Empty);
    }

    // Returns null while the authorization is still pending.
    private async Task<OAuthCredential?> PollDevice(DeviceAuthorization device, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["device_auth_id"] = device.DeviceAuthId.Value,
                ["user_code"] = device.UserCode.Value,
            },
            AuthWireContext.Default.DictionaryStringString);

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Issuer()}/api/accounts/deviceauth/token")
        {
            Content = content,
        };

        using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AuthException($"auth: device polling returned HTTP {(int)response.StatusCode}");
        }

        using var document = await ReadJson(response, cancellationToken).ConfigureAwait(false);
        var authorizationCode = ReadString(document.RootElement, "authorization_code");
        var codeVerifier = ReadString(document.RootElement, "code_verifier");

        if (authorizationCode.Length == 0 || codeVerifier.Length == 0)
        {
            throw new AuthException("auth: invalid device polling response");
        }

        return await Exchange(
            authorizationCode, $"{Issuer()}/deviceauth/callback", codeVerifier, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TokenResponse> TokenRequest(
        IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Issuer()}/oauth/token") { Content = content };
        using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new AuthException($"auth: token request returned HTTP {(int)response.StatusCode}");
        }

        using var document = await ReadJson(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var access = ReadString(root, "access_token");
        var refresh = ReadString(root, "refresh_token");

        if (access.Length == 0 || refresh.Length == 0)
        {
            throw new AuthException("auth: incomplete token response");
        }

        return new TokenResponse(ReadString(root, "id_token"), access, refresh, ReadLong(root, "expires_in"));
    }

    private OAuthCredential BuildCredential(TokenResponse tokens, string fallbackAccount)
    {
        var expires = tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600;
        var account = ExtractAccountId(tokens.IdToken);

        if (account.Length == 0)
        {
            account = ExtractAccountId(tokens.AccessToken);
        }

        if (account.Length == 0)
        {
            account = fallbackAccount;
        }

        return new OAuthCredential
        {
            AccessToken = new Secret(tokens.AccessToken),
            RefreshToken = new Secret(tokens.RefreshToken),
            ExpiresAt = Now().AddSeconds(expires),
            AccountId = account,
        };
    }

    private string Issuer() => options.Issuer.TrimEnd('/');

    private readonly record struct TokenResponse(string IdToken, string AccessToken, string RefreshToken, long ExpiresIn);
}
