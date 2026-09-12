using Parrot.Auth;
using Parrot.Config;
using Parrot.Llm.Wire;

namespace Parrot.Llm;

internal static class ProviderImplementations
{
    private static readonly IProviderImplementation Compatible = new CompatibleProviderImplementation(
        StandardModelDecoder.Instance,
        supportsProviderPreferences: false,
        static (options, client) => new OpenAICompatibleProvider(options, client));

    private static readonly IReadOnlyDictionary<string, IProviderImplementation> Known =
        new Dictionary<string, IProviderImplementation>(StringComparer.Ordinal)
        {
            ["openai"] = new CompatibleProviderImplementation(
                StandardModelDecoder.Instance,
                supportsProviderPreferences: false,
                static (options, client) => new OpenAICompatibleProvider(
                    options with { ImageTokenCalculator = OpenAiImageTokenCalculator.Instance }, client)),
            [ChatGptProvider.ProviderId] = new ChatGptProviderImplementation(),
            ["openrouter"] = new CompatibleProviderImplementation(
                OpenRouterModelDecoder.Instance,
                supportsProviderPreferences: true,
                static (options, client) => new OpenAICompatibleProvider(options, client)),
            ["opencode-go"] = new CompatibleProviderImplementation(
                StandardModelDecoder.Instance,
                supportsProviderPreferences: false,
                static (options, client) => new OpenCodeGoProvider(options, client)),
            ["kimi-code"] = new CompatibleProviderImplementation(
                KimiModelDecoder.Instance,
                supportsProviderPreferences: false,
                static (options, client) => new OpenAICompatibleProvider(options, client)),
            ["kimi-api"] = new CompatibleProviderImplementation(
                StandardModelDecoder.Instance,
                supportsProviderPreferences: false,
                static (options, client) => new KimiProvider(options, client)),
        };

    public static IProviderImplementation Resolve(string id) => Known.GetValueOrDefault(id) ?? Compatible;

    private sealed class ChatGptProviderImplementation : IProviderImplementation
    {
        public bool CanBuild(ProviderConfig config) => true;

        public BuiltProvider Build(ProviderBuildContext context)
        {
            var tokenSource = new OAuthTokenSource(
                context.CredentialStore,
                new OpenAiOAuthClient(context.HttpClient, context.BrowserOpener, new OpenAiOAuthOptions()),
                ChatGptProvider.ProviderId);
            return BuiltProvider.CreateWithSeed(new ChatGptProvider(
                tokenSource,
                context.HttpClient,
                ProviderModels.ReadDeclared(context.Id, context.Config.Models),
                ProviderModels.ReadDefaults(context.Id, context.Config.ModelDefaults),
                context.ExternalModels,
                context.Config.DisableWebSocket,
                new ResponsesWebSocketConnector())
            {
                MaximumRequestBytes = context.RequestLimits.ProviderRequestBytes,
                HeaderTimeout = TimeSpan.FromMilliseconds(context.Config.HeaderTimeoutMs),
            });
        }
    }

    private sealed class CompatibleProviderImplementation(
        IModelListDecoder decoder,
        bool supportsProviderPreferences,
        Func<OpenAICompatibleOptions, HttpClient, ILLMProvider> build) : IProviderImplementation
    {
        public bool CanBuild(ProviderConfig config) => config.BaseUrl.Length > 0;

        public BuiltProvider Build(ProviderBuildContext context)
        {
            var config = context.Config;
            var type = config.Type;
            if (type is not ("" or "compatible" or "openai-compatible"))
            {
                throw new LLMProviderException(
                    $"provider: unsupported provider type \"{type}\" for \"{context.Id}\"");
            }

            var options = new OpenAICompatibleOptions
            {
                Id = context.Id,
                MaximumRequestBytes = context.RequestLimits.ProviderRequestBytes,
                BaseUrl = config.BaseUrl,
                Protocol = config.Protocol switch
                {
                    "responses" => CompatibleProtocol.Responses,
                    "" or "chat-completions" => CompatibleProtocol.ChatCompletions,
                    _ => throw new LLMProviderException(
                        $"provider: unsupported protocol \"{config.Protocol}\" for \"{context.Id}\""),
                },
                ApiKeySource = new StoredApiKeySource(context.Id, config.ApiKeyEnv, context.CredentialStore),
                Headers = config.Headers,
                AllowInsecureLocalhost = config.AllowInsecureLocalhost,
                AllowInsecureRemote = config.AllowInsecureRemote,
                AllowInvalidTlsCertificate = config.AllowInvalidTlsCertificate,
                DisableWebSocket = config.DisableWebSocket,
                Models = ProviderModels.ReadDeclared(context.Id, config.Models),
                ModelDefaults = ProviderModels.ReadDefaults(context.Id, config.ModelDefaults),
                ExternalModels = context.ExternalModels,
                Decoder = decoder,
                HeaderTimeout = TimeSpan.FromMilliseconds(config.HeaderTimeoutMs),
                ProviderPreferences = supportsProviderPreferences ? config.ProviderPreferences : string.Empty,
            };
            var provider = build(options, context.HttpClient);
            return new(provider, provider.SeedModels());
        }
    }
}
