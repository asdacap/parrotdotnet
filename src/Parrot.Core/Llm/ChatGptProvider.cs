using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Parrot.Auth;
using Parrot.Llm.Wire;

namespace Parrot.Llm;

// The fixed ChatGPT subscription provider. It uses OAuth credentials only, with
// compiled-in endpoints and the responses dialect, and reports subscription
// usage. Port of Go's ChatGPT provider.
internal sealed class ChatGptProvider : ILLMProvider
{
    public const string ProviderId = "chatgpt";

    private const string StreamEndpoint = "https://chatgpt.com/backend-api/codex/responses";
    private const string ModelsEndpoint = "https://chatgpt.com/backend-api/codex/models";
    private const string ModelsClientVersion = "0.144.5";
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(10);

    private readonly IOAuthTokenSource _tokens;
    private readonly HttpClient _client;
    private readonly IReadOnlyList<LLMModel> _declared;
    private readonly IReadOnlyList<LLMModel> _defaults;
    private readonly IReadOnlyList<LLMModel> _external;
    private readonly Uri _endpoint = new(StreamEndpoint);
    private readonly string _sessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private readonly IResponsesWebSocketConnector _websocketConnector;
    private readonly bool _disableWebSocket;

    public ChatGptProvider(
        IOAuthTokenSource tokens,
        HttpClient client,
        IReadOnlyList<LLMModel> declared,
        IReadOnlyList<LLMModel> defaults,
        IReadOnlyList<LLMModel> external,
        bool disableWebSocket,
        IResponsesWebSocketConnector websocketConnector)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(websocketConnector);
        _tokens = tokens;
        UsageReporter = new ChatGptUsageReporter(tokens, client);
        _client = client;
        _declared = declared;
        _defaults = defaults;
        _external = external;
        _disableWebSocket = disableWebSocket;
        _websocketConnector = websocketConnector;
    }

    public IUsageReporter? UsageReporter { get; }

    public string Id => ProviderId;

    public IReadOnlyList<LLMModel> SeedModels() =>
        ChatGptModelCatalogue.ApplyLimits(ModelCatalogue.Merge(null, _declared, _defaults, _external));

    public ILLMProviderSession OpenSession()
    {
        var sessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        return new OpenAICompatibleProviderSession(
            static request => request with { MaxTokens = 0 },
            (request, turnState, captureTurnState, cancellationToken) =>
                CallHttp(sessionId, request, turnState, captureTurnState, cancellationToken),
            cancellationToken => AuthHeadersForSession(sessionId, cancellationToken),
            _disableWebSocket,
            new ResponsesWebSocketClient(
                _websocketConnector,
                _endpoint,
                HeaderTimeout,
                ResponsesWebSocket.DefaultIdleTimeout));
    }

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) =>
        _tokens.HasCredential(cancellationToken);

    public async Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken)
    {
        var access = await _tokens.Token(cancellationToken).ConfigureAwait(false);
        RequireToken(access);

        var uri = new Uri($"{ModelsEndpoint}?client_version={ModelsClientVersion}");
        var body = await HttpStreaming
            .Get(_client, uri, Headers(access), HttpStreaming.ModelsRefreshTimeout, 16 << 20, cancellationToken)
            .ConfigureAwait(false);

        return ChatGptModelCatalogue.ApplyLimits(
            ModelCatalogue.Merge(DecodeModels(body), _declared, _defaults, _external));
    }

    public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
        CallHttp(_sessionId, request, string.Empty, static _ => { }, cancellationToken);

    private static List<LLMModel> DecodeModels(string json)
    {
        using var document = JsonDocument.Parse(json);
        var models = new List<LLMModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (document.RootElement.TryGetProperty("models", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (JsonRead.String(item, "visibility") != "list")
                {
                    continue;
                }

                var slug = JsonRead.String(item, "slug");
                var contextWindow = JsonRead.Int(item, "context_window");

                if (contextWindow == 0)
                {
                    contextWindow = JsonRead.Int(item, "max_context_window");
                }

                if (slug.Length == 0 || contextWindow <= 0)
                {
                    throw new LLMProviderException($"provider: model catalog contains invalid listed model \"{slug}\"");
                }

                if (!seen.Add(slug))
                {
                    continue;
                }

                var fields = ModelMetadataFields.ContextWindow;
                var hasName = JsonRead.TryReadString(item, "display_name", out var name);
                fields |= hasName ? ModelMetadataFields.Name : ModelMetadataFields.None;
                var efforts = new List<string>();

                if (item.TryGetProperty("supported_reasoning_levels", out var levels)
                    && levels.ValueKind == JsonValueKind.Array)
                {
                    fields |= ModelMetadataFields.Reasoning | ModelMetadataFields.Variants;

                    foreach (var level in levels.EnumerateArray())
                    {
                        var effort = JsonRead.String(level, "effort");

                        if (effort.Length > 0 && !efforts.Contains(effort))
                        {
                            efforts.Add(effort);
                        }
                    }
                }

                var variants = efforts.Select(effort => new ModelVariant(effort, effort)).ToList();

                models.Add(new LLMModel(slug, ProviderId)
                {
                    Name = hasName ? name : slug,
                    ContextWindow = contextWindow,
                    Capabilities = new ModelCapabilities(
                        Tools: true, Reasoning: variants.Count > 0, Output: ["text"], Variants: variants),
                    Fields = fields,
                });
            }
        }

        return models.Count == 0
            ? throw new LLMProviderException("provider: models response contains no usable models")
            : models;
    }

    private static void RequireToken(OAuthAccess access)
    {
        if (access.AccessToken.Length == 0)
        {
            throw new LLMProviderException("provider: ChatGPT credential requires an access token");
        }
    }

    private static Dictionary<string, string> Headers(OAuthAccess access)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + access.AccessToken,
            ["originator"] = "parrot",
            ["User-Agent"] = "parrot",
        };

        if (access.AccountId.Length > 0)
        {
            headers["ChatGPT-Account-Id"] = access.AccountId;
        }

        return headers;
    }

    private async IAsyncEnumerable<LLMEvent> CallHttp(
        string sessionId,
        LLMRequest request,
        string turnState,
        Action<string> captureTurnState,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var access = await _tokens.Token(cancellationToken).ConfigureAwait(false);
        RequireToken(access);

        // ChatGPT does not support the max_output_tokens parameter.
        var body = ResponsesAdapter.Encode(request with { MaxTokens = 0 });
        var headers = Headers(access);
        headers["session-id"] = sessionId;
        if (turnState.Length > 0)
        {
            headers["x-codex-turn-state"] = turnState;
        }

        var response = await HttpStreaming
            .OpenStream(_client, _endpoint, body, headers, HeaderTimeout, cancellationToken)
            .ConfigureAwait(false);
        foreach (var header in response.Headers)
        {
            if (header.Key.Equals("x-codex-turn-state", StringComparison.OrdinalIgnoreCase))
            {
                captureTurnState(header.Value);
                break;
            }
        }

        await using (response.ConfigureAwait(false))
        {
            await foreach (var published in
                ResponsesAdapter.Parse(response.Content, HttpStreaming.MaxEventBytes, cancellationToken).ConfigureAwait(false))
            {
                yield return published;
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> AuthHeadersForSession(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var access = await _tokens.Token(cancellationToken).ConfigureAwait(false);
        RequireToken(access);
        var headers = Headers(access);
        headers["session-id"] = sessionId;
        return headers;
    }
}
