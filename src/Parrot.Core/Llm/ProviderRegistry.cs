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

    public ProviderRegistry(
        IReadOnlyList<ILLMProvider> providers,
        IReadOnlyDictionary<string, IReadOnlyList<LLMModel>> catalogues)
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

        _byId = byId;
        _ordered = [.. providers.OrderBy(provider => provider.Id, StringComparer.Ordinal)];
        _catalogues = new Dictionary<string, IReadOnlyList<LLMModel>>(catalogues, StringComparer.Ordinal);
    }

    public IReadOnlyList<ILLMProvider> List() => _ordered;

    public IReadOnlyList<LLMModel> Models(string providerId) =>
        _catalogues.TryGetValue(providerId, out var models) ? models : [];

    public IReadOnlyList<LLMModel> AllModels() =>
        [.. _ordered.SelectMany(provider => Models(provider.Id))];

    // Best effort: a provider that cannot be reached, or has no usable
    // credential, keeps the catalogue it was seeded with.
    public async Task RefreshAll(CancellationToken cancellationToken)
    {
        foreach (var provider in _ordered)
        {
            try
            {
                _catalogues[provider.Id] = await provider.ListModels(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (
                failure is LLMProviderException or Wire.ProviderHttpException or Wire.HeaderTimeoutException
                    or Wire.WireProtocolException or Auth.AuthException or HttpRequestException or IOException)
            {
                // A refresh failure is not fatal; the seeded catalogue stands in.
            }
        }
    }

    // Resolves a "provider/model" selection. An empty provider takes the first
    // sorted one; an empty model takes that provider's first sorted model. The
    // model portion keeps any vendor prefix, since selection splits on the first
    // slash only.
    public (ILLMProvider Provider, LLMModel Model) Resolve(string providerId, string modelId)
    {
        if (_ordered.Count == 0)
        {
            throw new LLMProviderException("provider: no providers configured");
        }

        var provider = providerId.Length == 0
            ? _ordered[0]
            : _byId.TryGetValue(providerId, out var found)
                ? found
                : throw new LLMProviderException($"provider: unknown provider \"{providerId}\"");

        var models = Models(provider.Id);

        if (models.Count == 0)
        {
            throw new LLMProviderException($"provider: \"{provider.Id}\" serves no models");
        }

        if (modelId.Length == 0)
        {
            return (provider, models.OrderBy(model => model.Id, StringComparer.Ordinal).First());
        }

        var model = models.FirstOrDefault(candidate => candidate.Id == modelId)
            ?? throw new LLMProviderException($"provider: unknown model \"{providerId}/{modelId}\"");

        return (provider, model);
    }
}
