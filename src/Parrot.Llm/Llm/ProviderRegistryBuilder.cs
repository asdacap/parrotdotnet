using Parrot.Auth;
using Parrot.Config;
using Parrot.Llm.ImageUpload;

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
        var imageBucket = configuration.ImageUpload is { } upload
            ? new S3ImageBucket(httpClients.Resolve(upload.Endpoint, false), upload, TimeProvider.System, store)
            : null;

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
                var modelsDevId = config.ModelsDevId.Length > 0 ? config.ModelsDevId : id;
                externalModels = externalCatalogues.TryGetValue(modelsDevId, out var models) ? models : [];
            }

            var built = implementation.Build(new(id, config, store, httpClient, browser, externalModels)
            {
                RequestLimits = configuration.RequestLimits,
            });
            var provider = imageBucket is not null && config.ImageUrls
                ? new ImageLinkingProvider(built.Provider, imageBucket)
                : built.Provider;
            providers.Add(new RetryingProvider(provider)
            {
                HeaderTimeoutMaxRetries = config.HeaderTimeoutMaxRetries,
            });
            catalogues[id] = built.Seed;
        }

        var registry = new ProviderRegistry(providers, catalogues);
        _ = await registry.AvailableModels(cancellationToken).ConfigureAwait(false);
        return registry;
    }
}
