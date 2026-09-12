using System.Runtime.CompilerServices;
using Parrot.Llm.Wire;

namespace Parrot.Llm;

// One provider for every endpoint speaking an OpenAI-compatible dialect
// (chat-completions or responses). It holds no conversation and nothing between
// calls. Port of Go's OpenAICompatible.
internal sealed class OpenAICompatibleProvider : ILLMProvider
{
    private readonly ImageGenerationClient _images;
    private readonly IImageTokenCalculator _imageTokenCalculator;
    private readonly Uri _endpoint;
    private readonly Uri _modelsEndpoint;
    private readonly Uri _modelInfoEndpoint;
    private readonly CompatibleProtocol _protocol;
    private readonly IApiKeySource _apiKeySource;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly IReadOnlyList<LLMModel> _declared;
    private readonly IReadOnlyList<LLMModel> _defaults;
    private readonly IReadOnlyList<LLMModel> _external;
    private readonly IModelListDecoder _decoder;
    private readonly HttpClient _client;
    private readonly TimeSpan _headerTimeout;
    private readonly string _providerPreferences;
    private readonly IResponsesWebSocketConnector _websocketConnector;
    private readonly bool _disableWebSocket;
    private readonly int _maximumRequestBytes;

    public OpenAICompatibleProvider(OpenAICompatibleOptions options, HttpClient client)
        : this(options, client, new ResponsesWebSocketConnector(options))
    {
    }

    internal OpenAICompatibleProvider(
        OpenAICompatibleOptions options,
        HttpClient client,
        IResponsesWebSocketConnector websocketConnector)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(websocketConnector);

        if (string.IsNullOrWhiteSpace(options.Id))
        {
            throw new LLMProviderException("provider: compatible provider ID is required");
        }

        ArgumentNullException.ThrowIfNull(options.ApiKeySource);

        if (options.HeaderTimeout < TimeSpan.Zero)
        {
            throw new LLMProviderException("provider: header timeout cannot be negative");
        }

        var endpointName = options.Protocol switch
        {
            CompatibleProtocol.Responses => "responses",
            CompatibleProtocol.ChatCompletions => "chat/completions",
            _ => throw new LLMProviderException("provider: protocol must be responses or chat-completions"),
        };

        Id = options.Id;
        _imageTokenCalculator = options.ImageTokenCalculator;
        _protocol = options.Protocol;
        _apiKeySource = options.ApiKeySource;
        _endpoint = HttpStreaming.EndpointUrl(
            options.BaseUrl, endpointName, options.AllowInsecureLocalhost, options.AllowInsecureRemote);
        _modelsEndpoint = HttpStreaming.EndpointUrl(
            options.BaseUrl, "models", options.AllowInsecureLocalhost, options.AllowInsecureRemote);
        _modelInfoEndpoint = HttpStreaming.EndpointUrl(
            options.BaseUrl, "model/info", options.AllowInsecureLocalhost, options.AllowInsecureRemote);
        _headers = HttpStreaming.ValidateHeaders(options.Headers);
        _declared = options.Models;
        _defaults = options.ModelDefaults;
        _external = options.ExternalModels;
        _decoder = options.Decoder;
        _client = client;
        _images = new ImageGenerationClient(
            client,
            HttpStreaming.EndpointUrl(options.BaseUrl, "images/generations", options.AllowInsecureLocalhost, options.AllowInsecureRemote),
            HttpStreaming.EndpointUrl(options.BaseUrl, "images/edits", options.AllowInsecureLocalhost, options.AllowInsecureRemote),
            AuthHeadersForSession);
        _headerTimeout = options.HeaderTimeout;
        _providerPreferences = options.ProviderPreferences;
        _websocketConnector = websocketConnector;
        _disableWebSocket = options.DisableWebSocket;
        _maximumRequestBytes = options.MaximumRequestBytes;
    }

    public string Id { get; }

    public long CalculateImageTokens(LLMModel model, LLMContent image) =>
        _imageTokenCalculator.CalculateImageTokens(model, image);

    public Task<ImageGenerationResult> GenerateImage(ImageGenerationRequest request, CancellationToken cancellationToken) =>
        _images.GenerateImage(request, cancellationToken);

    // The offline catalogue: what is selectable before the endpoint is reached.
    public IReadOnlyList<LLMModel> SeedModels() => ModelCatalogue.Merge(null, _declared, _defaults, _external);

    public ILLMProviderSession OpenSession() =>
        _protocol == CompatibleProtocol.Responses
            ? new OpenAICompatibleProviderSession(
                Prepare,
                CallHttp,
                AuthHeadersForSession,
                _disableWebSocket,
                new ResponsesWebSocketClient(
                    _websocketConnector,
                    _endpoint,
                    _headerTimeout,
                    ResponsesWebSocket.DefaultIdleTimeout,
                    _maximumRequestBytes))
            : new StatelessProviderSession(this);

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) =>
        _apiKeySource.HasCredential(cancellationToken);

    public async Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken)
    {
        var headers = await AuthHeaders(cancellationToken).ConfigureAwait(false);
        var body = await HttpStreaming
            .Get(_client, _modelsEndpoint, headers, HttpStreaming.ModelsRefreshTimeout, 16 << 20, cancellationToken)
            .ConfigureAwait(false);
        var models = _decoder.Decode(Id, body);

        if (LiteLlmModelInfoDecoder.NeedsSupplement(models))
        {
            models = await SupplementModels(models, headers, cancellationToken).ConfigureAwait(false);
        }

        return ModelCatalogue.Merge(models, _declared, _defaults, _external);
    }

    public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
        CallHttp(request, string.Empty, static _ => { }, cancellationToken);

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

    private static bool IsSupplementFailure(Exception failure) =>
        failure is LLMProviderException or ProviderHttpException or HeaderTimeoutException or WireProtocolException
            or System.Text.Json.JsonException or HttpRequestException or IOException;

    private async Task<IReadOnlyList<LLMModel>> SupplementModels(
        IReadOnlyList<LLMModel> models,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await HttpStreaming
                .Get(
                    _client,
                    _modelInfoEndpoint,
                    headers,
                    HttpStreaming.ModelsRefreshTimeout,
                    16 << 20,
                    cancellationToken)
                .ConfigureAwait(false);
            return ModelCatalogue.Supplement(models, LiteLlmModelInfoDecoder.Instance.Decode(Id, body));
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested && IsSupplementFailure(failure))
        {
            return models;
        }
    }

    private async IAsyncEnumerable<LLMEvent> CallHttp(
        LLMRequest request,
        string turnState,
        Action<string> captureTurnState,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prepared = Prepare(request);
        var body = _protocol == CompatibleProtocol.Responses
            ? ResponsesAdapter.Encode(prepared)
            : ChatCompletionsAdapter.Encode(prepared);

        var headers = await AuthHeaders(cancellationToken).ConfigureAwait(false);
        if (turnState.Length > 0)
        {
            headers["x-codex-turn-state"] = turnState;
        }

        if (body.Length > _maximumRequestBytes)
        {
            throw new ProviderHttpException($"provider: request exceeds {_maximumRequestBytes} bytes");
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

        async IAsyncEnumerable<LLMEvent> Send(ProviderAttemptDiagnostics? attempt, [EnumeratorCancellation] CancellationToken sendCancellationToken)
        {
            attempt?.RecordRequestBytes(body.Length);
            request.Diagnostics?.DumpRequest(body);
            yield return LLMEvent.HttpRequestStarted();
            var response = await HttpStreaming
                .OpenStream(_client, _endpoint, body, headers, _headerTimeout, _maximumRequestBytes, attempt, sendCancellationToken)
                .ConfigureAwait(false);
            CaptureTurnState(response.Headers, captureTurnState);

            await using (response.ConfigureAwait(false))
            {
                yield return LLMEvent.HttpResponseHeadersReceived();
                var responseEvents = _protocol == CompatibleProtocol.Responses
                    ? ResponsesAdapter.Parse(response.Content, HttpStreaming.MaxEventBytes, sendCancellationToken)
                    : ChatCompletionsAdapter.Parse(response.Content, HttpStreaming.MaxEventBytes, sendCancellationToken);

                await foreach (var published in responseEvents.ConfigureAwait(false))
                {
                    yield return published;
                }
            }
        }
    }

    private LLMRequest Prepare(LLMRequest request) => request with
    {
        ProviderPreferences = _providerPreferences,
        IncludeRouterMetadata = _providerPreferences.Length > 0,
    };

    private async Task<IReadOnlyDictionary<string, string>> AuthHeadersForSession(CancellationToken cancellationToken) =>
        await AuthHeaders(cancellationToken).ConfigureAwait(false);

    private async Task<Dictionary<string, string>> AuthHeaders(CancellationToken cancellationToken)
    {
        var apiKey = await _apiKeySource.ApiKey(cancellationToken).ConfigureAwait(false);

        if (apiKey.Length == 0)
        {
            throw new LLMProviderException($"provider: \"{Id}\" has no API key");
        }

        return new Dictionary<string, string>(_headers, StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + apiKey,
        };
    }
}
