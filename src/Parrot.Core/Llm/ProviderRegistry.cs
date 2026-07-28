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

    // Resolves one complete canonical selector. Model IDs may contain slashes,
    // so an exact model match wins before the final segment is considered as a
    // variant name. Unlisted model IDs pass through to the provider.
    public ProviderModel ResolveCanonical(string selector)
    {
        if (selector.Length == 0)
        {
            throw new LLMProviderException("provider: a canonical model selector is required");
        }

        var slash = selector.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == selector.Length - 1 || selector.Contains("//", StringComparison.Ordinal))
        {
            throw new LLMProviderException($"provider: malformed model selector \"{selector}\"");
        }

        var providerId = selector[..slash];
        var modelSelector = selector[(slash + 1)..];
        var provider = _byId.TryGetValue(providerId, out var found)
            ? found
            : throw new LLMProviderException($"provider: unknown provider \"{providerId}\"");
        var models = Models(provider.Id);

        var exact = models.FirstOrDefault(candidate => candidate.Id == modelSelector);
        if (exact is not null)
        {
            return new ProviderModel(provider, exact);
        }

        var matches = models
            .SelectMany(model => model.Capabilities.Variants
                .Where(variant => string.Equals(
                    modelSelector, $"{model.Id}/{variant.Name}", StringComparison.Ordinal))
                .Select(variant => new ProviderModel(provider, model, variant)))
            .Take(2)
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            throw new LLMProviderException($"provider: ambiguous model selector \"{selector}\"");
        }

        var variantSlash = modelSelector.LastIndexOf('/');
        if (variantSlash > 0)
        {
            var variantName = modelSelector[(variantSlash + 1)..];
            var variants = models
                .SelectMany(model => model.Capabilities.Variants)
                .Where(variant => variant.Name == variantName)
                .Distinct()
                .Take(2)
                .ToList();

            if (variants.Count == 1)
            {
                return new ProviderModel(
                    provider,
                    new LLMModel(modelSelector[..variantSlash], provider.Id),
                    variants[0]);
            }
        }

        return new ProviderModel(provider, new LLMModel(modelSelector, provider.Id));
    }

    public ProviderModel ResolveDefaultCanonical()
    {
        if (_ordered.Count == 0)
        {
            throw new LLMProviderException("provider: no providers configured");
        }

        return ResolveDefaultForProvider(_ordered[0]);
    }

    private static bool IsRefreshFailure(Exception failure) =>
        failure is LLMProviderException or Wire.ProviderHttpException or Wire.HeaderTimeoutException
            or Wire.WireProtocolException or Auth.AuthException or HttpRequestException or IOException;

    private ProviderModel ResolveDefaultForProvider(ILLMProvider provider)
    {
        var models = Models(provider.Id);
        if (models.Count == 0)
        {
            throw new LLMProviderException($"provider: \"{provider.Id}\" serves no models");
        }

        return new ProviderModel(provider, models.OrderBy(candidate => candidate.Id, StringComparer.Ordinal).First());
    }
}
