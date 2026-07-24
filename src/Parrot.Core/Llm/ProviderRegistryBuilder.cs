using Parrot.Auth;
using Parrot.Config;

namespace Parrot.Llm;

// Builds the provider registry from presets, configuration, and stored
// credentials. The ChatGPT OAuth provider is always present; API-key providers
// are built regardless of whether a credential currently exists, and resolve it
// per request. Every provider is wrapped so its calls retry. Port of Go's
// app.BuildProviders.
internal sealed class ProviderRegistryBuilder(
    Configuration configuration,
    ICredentialStore store,
    HttpClient httpClient,
    IBrowserOpener browser)
{
    public Task<ProviderRegistry> Build()
    {
        var chatgpt = new ChatGptProvider(ChatGptTokens(), httpClient);
        var providers = new List<ILLMProvider> { new RetryingProvider(chatgpt) };
        var catalogues = new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
        {
            [ChatGptProvider.ProviderId] = ChatGptProvider.SeedModels(),
        };

        var configured = configuration.Providers;
        var ids = configured.Keys
            .Concat(ProviderPresets.PresetOnlyIds(configured.Keys))
            .Where(id => id != ChatGptProvider.ProviderId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal);

        foreach (var id in ids)
        {
            var built = BuildOne(id, configured.GetValueOrDefault(id));

            if (built is not null)
            {
                providers.Add(new RetryingProvider(built.Provider));
                catalogues[id] = built.Seed;
            }
        }

        return Task.FromResult(new ProviderRegistry(providers, catalogues, ResolveDefaultModel(providers, catalogues)));
    }

    private static string ProviderPreferences(ProviderConfig? config, ProviderPreset? preset) =>
        preset?.SupportsProviderPreferences == true ? config?.ProviderPreferences ?? string.Empty : string.Empty;

    private static CompatibleProtocol ResolveProtocol(string? configured, CompatibleProtocol? preset) =>
        configured switch
        {
            "responses" => CompatibleProtocol.Responses,
            "chat-completions" => CompatibleProtocol.ChatCompletions,
            _ => preset ?? CompatibleProtocol.ChatCompletions,
        };

    private static TimeSpan ResolveHeaderTimeout(int? configuredMs, TimeSpan? preset) =>
        configuredMs is { } ms ? TimeSpan.FromMilliseconds(ms) : preset ?? TimeSpan.Zero;

    private static IReadOnlyList<LLMModel> DeclaredModels(string providerId, ProviderConfig? config)
    {
        if (config is null || config.Models.Count == 0)
        {
            return [];
        }

        return
        [
            .. config.Models.Select(entry => new LLMModel(entry.Key, providerId)
            {
                Name = entry.Value.Name,
                ContextWindow = entry.Value.Context,
                MaxOutputTokens = entry.Value.MaxTokens,
                Capabilities = new ModelCapabilities(
                    entry.Value.Tools,
                    entry.Value.Reasoning || entry.Value.Variants.Count > 0,
                    entry.Value.Output.Count > 0 ? entry.Value.Output : ["text"],
                    [.. entry.Value.Variants
                        .OrderBy(variant => variant.Key, StringComparer.Ordinal)
                        .Select(variant => new ModelVariant(variant.Key, variant.Value))]),
            }),
        ];
    }

    private static string FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrEmpty(first) ? first : second ?? string.Empty;

    private static BuiltProvider Built(OpenCodeGoProvider provider) => new(provider, provider.SeedModels());

    private static BuiltProvider Built(KimiProvider provider) => new(provider, provider.SeedModels());

    private static BuiltProvider Built(OpenAICompatibleProvider provider) => new(provider, provider.SeedModels());

    private ProviderModel? ResolveDefaultModel(
        IReadOnlyList<ILLMProvider> providers,
        IReadOnlyDictionary<string, IReadOnlyList<LLMModel>> catalogues)
    {
        if (string.IsNullOrEmpty(configuration.Model))
        {
            return null;
        }

        var slash = configuration.Model.IndexOf('/', StringComparison.Ordinal);
        var providerId = slash < 0 ? string.Empty : configuration.Model[..slash];
        var modelId = slash < 0 ? configuration.Model : configuration.Model[(slash + 1)..];

        if (modelId.Length == 0)
        {
            return null;
        }

        var provider = providerId.Length == 0
            ? providers.FirstOrDefault(candidate => catalogues.GetValueOrDefault(candidate.Id, []).Any(model => model.Id == modelId))
            : providers.FirstOrDefault(candidate => candidate.Id == providerId);

        if (provider is null)
        {
            return null;
        }

        var model = catalogues.GetValueOrDefault(provider.Id, []).FirstOrDefault(candidate => candidate.Id == modelId);
        return model is null ? null : new ProviderModel(provider, model);
    }

    private OAuthTokenSource ChatGptTokens() =>
        new(
            store,
            new OpenAiOAuthClient(httpClient, browser, new OpenAiOAuthOptions()),
            ChatGptProvider.ProviderId);

    private BuiltProvider? BuildOne(string id, ProviderConfig? config)
    {
        var preset = ProviderPresets.All.GetValueOrDefault(id);
        var baseUrl = FirstNonEmpty(config?.BaseUrl, preset?.BaseUrl);

        if (baseUrl.Length == 0)
        {
            return null;
        }

        var type = config?.Type ?? string.Empty;

        if (type is not ("" or "compatible" or "openai-compatible"))
        {
            throw new LLMProviderException($"provider: unsupported provider type \"{type}\" for \"{id}\"");
        }

        var apiKeyEnv = FirstNonEmpty(config?.ApiKeyEnv, preset?.ApiKeyEnv);
        var options = new OpenAICompatibleOptions
        {
            Id = id,
            BaseUrl = baseUrl,
            Protocol = ResolveProtocol(config?.Protocol, preset?.Protocol),
            ApiKeySource = new StoredApiKeySource(id, apiKeyEnv, store),
            Headers = config?.Headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
            AllowInsecureLocalhost = config?.AllowInsecureLocalhost ?? false,
            Models = DeclaredModels(id, config),
            ModelDefaults = preset?.ModelDefaults ?? [],
            Decoder = preset?.Decoder ?? StandardModelDecoder.Instance,
            HeaderTimeout = ResolveHeaderTimeout(config?.HeaderTimeoutMs, preset?.HeaderTimeout),
            ProviderPreferences = ProviderPreferences(config, preset),
        };

        return id switch
        {
            "opencode-go" => Built(new OpenCodeGoProvider(options, httpClient)),
            "kimi-api" => Built(new KimiProvider(options, httpClient)),
            _ => Built(new OpenAICompatibleProvider(options, httpClient)),
        };
    }

    // A provider and the catalogue it is selectable with before its endpoint is
    // reached; the registry owns both from here on.
    private sealed record BuiltProvider(ILLMProvider Provider, IReadOnlyList<LLMModel> Seed);
}
