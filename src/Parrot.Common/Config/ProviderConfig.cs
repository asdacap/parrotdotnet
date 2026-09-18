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

    public bool AllowInsecureRemote { get; init; }

    public bool AllowInvalidTlsCertificate { get; init; }

    public bool DisableWebSocket { get; init; } = true;

    public int StreamIdleTimeoutMs { get; init; } = 300000;

    public int HeaderTimeoutMs { get; init; } = 60000;

    public int HeaderTimeoutMaxRetries { get; init; } = 5;

    // Opaque JSON object forwarded as the request body's "provider" field.
    public string ProviderPreferences { get; init; } = string.Empty;

    // Header carrying a random id that is stable for one provider session; empty sends none.
    public string SessionHeader { get; init; } = string.Empty;

    // models.dev provider whose catalog supplies this provider's model metadata; empty means this provider's own id.
    public string ModelsDevId { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, ModelConfig> ModelDefaults { get; init; } =
        new Dictionary<string, ModelConfig>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ModelConfig> Models { get; init; } =
        new Dictionary<string, ModelConfig>(StringComparer.Ordinal);
}
