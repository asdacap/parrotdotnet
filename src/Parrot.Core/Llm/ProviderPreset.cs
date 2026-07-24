namespace Parrot.Llm;

// Built-in defaults for a well-known provider id, so a user only has to supply a
// credential. Every configured field overrides the preset; the preset fills only
// what was left empty. Model defaults describe models rather than declaring them.
internal sealed record ProviderPreset
{
    public CompatibleProtocol Protocol { get; init; } = CompatibleProtocol.ChatCompletions;

    public string BaseUrl { get; init; } = string.Empty;

    public string ApiKeyEnv { get; init; } = string.Empty;

    public TimeSpan HeaderTimeout { get; init; }

    public IReadOnlyList<LLMModel> ModelDefaults { get; init; } = [];

    public IModelListDecoder Decoder { get; init; } = StandardModelDecoder.Instance;

    public bool SupportsProviderPreferences { get; init; }
}
