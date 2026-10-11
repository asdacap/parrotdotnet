using System.Runtime.CompilerServices;
using Parrot.Llm.Wire;

namespace Parrot.Llm;

// One provider for every endpoint speaking the Anthropic Messages API. It holds
// no conversation and nothing between calls.
internal sealed class AnthropicProvider : ILLMProvider
{
    private const string ApiVersion = "2023-06-01";

    // Lets requests set thinking.block_binding.
    private const string BindingControlsBeta = "thinking-binding-controls-2026-08-01";

    private readonly AnthropicOptions _options;
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly Uri _modelsEndpoint;
    private readonly IReadOnlyDictionary<string, string> _headers;

    public AnthropicProvider(AnthropicOptions options, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Id))
        {
            throw new LLMProviderException("provider: anthropic provider ID is required");
        }

        _options = options;
        _client = client;
        _endpoint = HttpStreaming.EndpointUrl(
            options.BaseUrl, "messages", options.AllowInsecureLocalhost, options.AllowInsecureRemote);
        _modelsEndpoint = new UriBuilder(HttpStreaming.EndpointUrl(
            options.BaseUrl, "models", options.AllowInsecureLocalhost, options.AllowInsecureRemote))
        {
            Query = "limit=1000",
        }.Uri;
        _headers = HttpStreaming.ValidateHeaders(options.Headers);
    }

    public string Id => _options.Id;

    public IReadOnlyList<LLMModel> SeedModels() =>
        ModelCatalogue.Merge(null, _options.Models, _options.ModelDefaults, _options.ExternalModels);

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) =>
        _options.ApiKeySource.HasCredential(cancellationToken);

    public async Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken)
    {
        var headers = await Headers(cancellationToken).ConfigureAwait(false);
        var body = await HttpStreaming
            .Get(_client, _modelsEndpoint, headers, HttpStreaming.ModelsRefreshTimeout, 16 << 20, cancellationToken)
            .ConfigureAwait(false);
        return ModelCatalogue.Merge(
            StandardModelDecoder.Instance.Decode(Id, body), _options.Models, _options.ModelDefaults, _options.ExternalModels);
    }

    public async IAsyncEnumerable<LLMEvent> Call(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = AnthropicMessagesAdapter.Encode(request, Id);
        var headers = await Headers(cancellationToken).ConfigureAwait(false);

        await foreach (var published in HttpStreaming
            .StreamEvents(
                _client,
                _endpoint,
                body,
                headers,
                _options.HeaderTimeout,
                _options.StreamIdleTimeout,
                _options.MaximumRequestBytes,
                request,
                static _ => { },
                (stream, maxEventBytes, parseCancellationToken) =>
                    AnthropicMessagesAdapter.Parse(stream, maxEventBytes, Id, parseCancellationToken),
                cancellationToken)
            .ConfigureAwait(false))
        {
            yield return published;
        }
    }

    private async Task<Dictionary<string, string>> Headers(CancellationToken cancellationToken)
    {
        var apiKey = await _options.ApiKeySource.ApiKey(cancellationToken).ConfigureAwait(false);

        if (apiKey.Length == 0)
        {
            throw new LLMProviderException($"provider: \"{Id}\" has no API key");
        }

        var headers = new Dictionary<string, string>(_headers, StringComparer.OrdinalIgnoreCase)
        {
            ["x-api-key"] = apiKey,
        };
        _ = headers.TryAdd("anthropic-version", ApiVersion);
        headers["anthropic-beta"] = headers.TryGetValue("anthropic-beta", out var betas) && betas.Length > 0
            ? $"{betas},{BindingControlsBeta}"
            : BindingControlsBeta;
        _ = headers.TryAdd("User-Agent", $"{BuildInfo.ProductName}/{BuildInfo.Version}");
        return headers;
    }
}
