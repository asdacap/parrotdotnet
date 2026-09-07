using Parrot.Auth;
using Parrot.Config;

namespace Parrot.Llm;

internal sealed class ProviderRegistryBuilder(
    Configuration configuration,
    ICredentialStore store,
    ProviderHttpClientCatalog httpClients,
    IBrowserOpener browser,
    ModelsDevInformationProvider modelsDev)
{
    public static IReadOnlyList<string> BuildableProviderIds(Configuration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return
        [
            .. configuration.Providers
                .Where(entry => ProviderImplementations.Resolve(entry.Key).CanBuild(entry.Value))
                .Select(entry => entry.Key)
                .OrderBy(id => id, StringComparer.Ordinal),
        ];
    }

    public async Task<ProviderRegistry> Build(CancellationToken cancellationToken)
    {
        var providers = new List<ILLMProvider>();
        var catalogues = new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal);
        var externalCatalogues = await modelsDev.Fetch(cancellationToken).ConfigureAwait(false);

        foreach (var id in BuildableProviderIds(configuration))
        {
            var config = configuration.Providers[id];
            var implementation = ProviderImplementations.Resolve(id);
            var httpClient = httpClients.Resolve(config.BaseUrl, config.AllowInvalidTlsCertificate);
            IReadOnlyList<LLMModel> externalModels;
            if (id == ChatGptProvider.ProviderId)
            {
                var openAiModels = externalCatalogues.TryGetValue("openai", out var models) ? models : [];
                externalModels = ChatGptModelCatalogue.FilterExternal(openAiModels);
            }
            else
            {
                externalModels = externalCatalogues.TryGetValue(id, out var models) ? models : [];
            }

            var built = implementation.Build(new(id, config, store, httpClient, browser, externalModels));
            providers.Add(new RetryingProvider(built.Provider));
            catalogues[id] = built.Seed;
        }

        var registry = new ProviderRegistry(providers, catalogues);
        _ = await registry.AvailableModels(cancellationToken).ConfigureAwait(false);
        return registry;
    }
}
