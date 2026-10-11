using Parrot.Config;

namespace Parrot.Llm;

// Configures an API-key authenticated Anthropic Messages provider.
internal sealed record AnthropicOptions
{
    public int MaximumRequestBytes { get; init; } = new RequestLimitsConfig().ProviderRequestBytes;

    public required string Id { get; init; }

    public required string BaseUrl { get; init; }

    public required IApiKeySource ApiKeySource { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public bool AllowInsecureLocalhost { get; init; }

    public bool AllowInsecureRemote { get; init; }

    public IReadOnlyList<LLMModel> Models { get; init; } = [];

    public IReadOnlyList<LLMModel> ModelDefaults { get; init; } = [];

    public IReadOnlyList<LLMModel> ExternalModels { get; init; } = [];

    public TimeSpan StreamIdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
