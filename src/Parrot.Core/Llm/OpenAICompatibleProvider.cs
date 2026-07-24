using System.Runtime.CompilerServices;
using Parrot.Llm.Wire;

namespace Parrot.Llm;

// One provider for every endpoint speaking an OpenAI-compatible dialect
// (chat-completions or responses). It holds no conversation and nothing between
// calls. Port of Go's OpenAICompatible.
internal sealed class OpenAICompatibleProvider : ILLMProvider
{
    private readonly Uri _endpoint;
    private readonly Uri _modelsEndpoint;
    private readonly CompatibleProtocol _protocol;
    private readonly IApiKeySource _apiKeySource;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly IReadOnlyList<LLMModel> _declared;
    private readonly IReadOnlyList<LLMModel> _defaults;
    private readonly IModelListDecoder _decoder;
    private readonly HttpClient _client;
    private readonly TimeSpan _headerTimeout;
    private readonly string _providerPreferences;

    public OpenAICompatibleProvider(OpenAICompatibleOptions options, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(options);

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
        _protocol = options.Protocol;
        _apiKeySource = options.ApiKeySource;
        _endpoint = HttpStreaming.EndpointUrl(options.BaseUrl, endpointName, options.AllowInsecureLocalhost);
        _modelsEndpoint = HttpStreaming.EndpointUrl(options.BaseUrl, "models", options.AllowInsecureLocalhost);
        _headers = HttpStreaming.ValidateHeaders(options.Headers);
        _declared = options.Models;
        _defaults = options.ModelDefaults;
        _decoder = options.Decoder;
        _client = client;
        _headerTimeout = options.HeaderTimeout;
        _providerPreferences = options.ProviderPreferences;
    }

    public string Id { get; }

    // The offline catalogue: what is selectable before the endpoint is reached.
    public IReadOnlyList<LLMModel> SeedModels() => ModelCatalogue.Merge(null, _declared, _defaults);

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) =>
        _apiKeySource.HasCredential(cancellationToken);

    public async Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken)
    {
        var body = await HttpStreaming
            .Get(_client, _modelsEndpoint, await AuthHeaders(cancellationToken).ConfigureAwait(false), HttpStreaming.ModelsRefreshTimeout, 16 << 20, cancellationToken)
            .ConfigureAwait(false);

        return ModelCatalogue.Merge(_decoder.Decode(Id, body), _declared, _defaults);
    }

    public async IAsyncEnumerable<LLMEvent> Call(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prepared = request with
        {
            ProviderPreferences = _providerPreferences,
            IncludeRouterMetadata = _providerPreferences.Length > 0,
        };

        var body = _protocol == CompatibleProtocol.Responses
            ? ResponsesAdapter.Encode(prepared)
            : ChatCompletionsAdapter.Encode(prepared);

        var stream = await HttpStreaming
            .OpenStream(_client, _endpoint, body, await AuthHeaders(cancellationToken).ConfigureAwait(false), _headerTimeout, cancellationToken)
            .ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            var events = _protocol == CompatibleProtocol.Responses
                ? ResponsesAdapter.Parse(stream, HttpStreaming.MaxEventBytes, cancellationToken)
                : ChatCompletionsAdapter.Parse(stream, HttpStreaming.MaxEventBytes, cancellationToken);

            await foreach (var published in events.ConfigureAwait(false))
            {
                yield return published;
            }
        }
    }

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
