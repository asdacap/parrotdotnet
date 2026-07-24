namespace Parrot.Config;

// A configured OpenAI-compatible provider. Credential values never live here;
// ApiKeyEnv names an environment variable instead. Port of Go's config.Provider.
internal sealed record ProviderConfig
{
    public string Type { get; init; } = string.Empty;

    public string Protocol { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = string.Empty;

    public string ApiKeyEnv { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public bool AllowInsecureLocalhost { get; init; }

    public int? HeaderTimeoutMs { get; init; }

    // Opaque JSON object forwarded as the request body's "provider" field.
    public string ProviderPreferences { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, ModelConfig> Models { get; init; } =
        new Dictionary<string, ModelConfig>(StringComparer.Ordinal);
}
