using Parrot.Auth;
using Parrot.Config;

namespace Parrot.Llm;

// Builds the provider registry from presets, configuration, and stored
// credentials. The ChatGPT OAuth provider is always present; API-key providers
// are built when a credential (or its environment variable) is available. Every
// provider is wrapped so its calls retry. Port of Go's app.BuildProviders.
internal sealed class ProviderRegistryBuilder(
    Configuration configuration,
    ICredentialStore store,
    HttpClient httpClient,
    IBrowserOpener browser)
{
    public async Task<ProviderRegistry> Build(CancellationToken cancellationToken)
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
            var built = await BuildOne(
                id, configured.GetValueOrDefault(id), configured.ContainsKey(id), cancellationToken).ConfigureAwait(false);

            if (built is not null)
            {
                providers.Add(new RetryingProvider(built.Provider));
                catalogues[id] = built.Seed;
            }
        }

        return new ProviderRegistry(providers, catalogues);
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

    private OAuthTokenSource ChatGptTokens() =>
        new(
            store,
            new OpenAiOAuthClient(httpClient, browser, new OpenAiOAuthOptions()),
            ChatGptProvider.ProviderId);

    private async Task<BuiltProvider?> BuildOne(
        string id, ProviderConfig? config, bool explicitlyConfigured, CancellationToken cancellationToken)
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
        var apiKey = await ResolveApiKey(id, apiKeyEnv, cancellationToken).ConfigureAwait(false);

        if (apiKey.Length == 0)
        {
            if (explicitlyConfigured)
            {
                throw new LLMProviderException($"provider: \"{id}\" has no API key (set {apiKeyEnv} or store a credential)");
            }

            // An optional preset without a credential is silently skipped.
            return null;
        }

        var options = new OpenAICompatibleOptions
        {
            Id = id,
            BaseUrl = baseUrl,
            Protocol = ResolveProtocol(config?.Protocol, preset?.Protocol),
            ApiKey = apiKey,
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

    private async Task<string> ResolveApiKey(string id, string apiKeyEnv, CancellationToken cancellationToken)
    {
        if (apiKeyEnv.Length > 0 && Environment.GetEnvironmentVariable(apiKeyEnv) is { Length: > 0 } fromEnv)
        {
            return fromEnv;
        }

        var credential = await store.Get(id, cancellationToken).ConfigureAwait(false);
        return credential is { Type: CredentialType.ApiKey, ApiKey: { } apiKey } ? apiKey.Key.Value : string.Empty;
    }

    // A provider and the catalogue it is selectable with before its endpoint is
    // reached; the registry owns both from here on.
    private sealed record BuiltProvider(ILLMProvider Provider, IReadOnlyList<LLMModel> Seed);
}
