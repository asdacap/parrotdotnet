namespace Parrot.Llm;

// Configures an API-key authenticated OpenAI-compatible provider.
internal sealed record OpenAICompatibleOptions
{
    public required string Id { get; init; }

    public required string BaseUrl { get; init; }

    public CompatibleProtocol Protocol { get; init; } = CompatibleProtocol.ChatCompletions;

    public required IApiKeySource ApiKeySource { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public bool AllowInsecureLocalhost { get; init; }

    public bool AllowInsecureRemote { get; init; }

    public bool AllowInvalidTlsCertificate { get; init; }

    public bool DisableWebSocket { get; init; } = true;

    // Declared by the user; always selectable, even when the endpoint does not
    // list them.
    public IReadOnlyList<LLMModel> Models { get; init; } = [];

    // Metadata a model list cannot express. Describes models rather than
    // declaring them: dropped once a catalogue is fetched if the endpoint does
    // not serve the id, and only survives as an offline fallback.
    public IReadOnlyList<LLMModel> ModelDefaults { get; init; } = [];

    public IReadOnlyList<LLMModel> ExternalModels { get; init; } = [];

    public IModelListDecoder Decoder { get; init; } = StandardModelDecoder.Instance;

    public TimeSpan HeaderTimeout { get; init; }

    // Forwarded as the top-level "provider" object of each request body. Empty
    // unless the provider supports routing preferences (OpenRouter).
    public string ProviderPreferences { get; init; } = string.Empty;
}
