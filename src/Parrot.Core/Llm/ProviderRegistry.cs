namespace Parrot.Llm;

// The configured and built-in providers and their model catalogues. The
// catalogue lives here rather than on ILLMProvider so a provider stays the
// stateless thing it is: it can list models, but remembering them is the
// registry's job. Port of Go's agent.ProviderRegistry.
internal sealed class ProviderRegistry
{
    private readonly Dictionary<string, ILLMProvider> _byId;
    private readonly IReadOnlyList<ILLMProvider> _ordered;
    private readonly Dictionary<string, IReadOnlyList<LLMModel>> _catalogues;
    private readonly ProviderModel? _defaultModel;

    public ProviderRegistry(
        IReadOnlyList<ILLMProvider> providers,
        IReadOnlyDictionary<string, IReadOnlyList<LLMModel>> catalogues,
        ProviderModel? defaultModel)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(catalogues);

        var byId = new Dictionary<string, ILLMProvider>(StringComparer.Ordinal);

        foreach (var provider in providers)
        {
            if (string.IsNullOrEmpty(provider.Id) || !byId.TryAdd(provider.Id, provider))
            {
                throw new LLMProviderException($"provider: duplicate or unnamed provider \"{provider.Id}\"");
            }
        }

        if (defaultModel is not null && !ReferenceEquals(defaultModel.Provider, byId.GetValueOrDefault(defaultModel.Provider.Id)))
        {
            throw new LLMProviderException($"provider: default provider \"{defaultModel.Provider.Id}\" is not registered");
        }

        _byId = byId;
        _ordered = [.. providers.OrderBy(provider => provider.Id, StringComparer.Ordinal)];
        _catalogues = new Dictionary<string, IReadOnlyList<LLMModel>>(catalogues, StringComparer.Ordinal);
        _defaultModel = defaultModel;
    }

    public IReadOnlyList<ILLMProvider> List() => _ordered;

    public IReadOnlyList<ProviderModel> AllModels() =>
        [.. _ordered.SelectMany(provider => Models(provider.Id).Select(model => new ProviderModel(provider, model)))];

    public IReadOnlyList<LLMModel> Models(string providerId) =>
        _catalogues.TryGetValue(providerId, out var models) ? models : [];

    public async Task<IReadOnlyList<ProviderModel>> AvailableModels(CancellationToken cancellationToken)
    {
        var available = new List<ProviderModel>();

        foreach (var provider in _ordered)
        {
            bool hasCredential;

            try
            {
                hasCredential = await provider.HasCredential(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (IsRefreshFailure(failure))
            {
                continue;
            }

            if (!hasCredential)
            {
                continue;
            }

            try
            {
                _catalogues[provider.Id] = await provider.ListModels(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (IsRefreshFailure(failure))
            {
                // A refresh failure is not fatal; the seeded catalogue stands in.
            }

            available.AddRange(Models(provider.Id).Select(model => new ProviderModel(provider, model)));
        }

        return available;
    }

    // Resolves a "provider/model" selection. An empty provider takes the
    // configured default when available, otherwise the first sorted one; an empty
    // model takes that provider's default when available, otherwise its first
    // sorted model. The model portion keeps any vendor prefix, since selection
    // splits on the first slash only.
    public ProviderModel Resolve(string providerId, string modelId)
    {
        if (_ordered.Count == 0)
        {
            throw new LLMProviderException("provider: no providers configured");
        }

        if (providerId.Length == 0)
        {
            if (modelId.Length == 0 && _defaultModel is not null)
            {
                return _defaultModel;
            }

            var fallbackProvider = _defaultModel?.Provider ?? _ordered[0];
            return ResolveForProvider(fallbackProvider, modelId);
        }

        var provider = _byId.TryGetValue(providerId, out var found)
            ? found
            : throw new LLMProviderException($"provider: unknown provider \"{providerId}\"");

        return ResolveForProvider(provider, modelId);
    }

    private static bool IsRefreshFailure(Exception failure) =>
        failure is LLMProviderException or Wire.ProviderHttpException or Wire.HeaderTimeoutException
            or Wire.WireProtocolException or Auth.AuthException or HttpRequestException or IOException;

    private ProviderModel ResolveForProvider(ILLMProvider provider, string modelId)
    {
        var models = Models(provider.Id);

        if (models.Count == 0)
        {
            throw new LLMProviderException($"provider: \"{provider.Id}\" serves no models");
        }

        if (modelId.Length == 0)
        {
            var model = _defaultModel is { Provider.Id: var defaultProviderId, Model: var defaultModel } && defaultProviderId == provider.Id
                ? defaultModel
                : models.OrderBy(candidate => candidate.Id, StringComparer.Ordinal).First();
            return new ProviderModel(provider, model);
        }

        var resolved = models.FirstOrDefault(candidate => candidate.Id == modelId)
            ?? throw new LLMProviderException($"provider: unknown model \"{provider.Id}/{modelId}\"");

        return new ProviderModel(provider, resolved);
    }
}
