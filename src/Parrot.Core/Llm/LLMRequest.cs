namespace Parrot.Llm;

internal sealed record LLMRequest
{
    public required string Model { get; init; }

    public required IReadOnlyList<LLMMessage> Messages { get; init; }

    public IReadOnlyList<LLMToolDefinition> Tools { get; init; } = [];

    public int MaxTokens { get; init; }

    // Top-level system instructions, carried separately from the message list
    // because the responses dialect wants them out of band.
    public string Instructions { get; init; } = string.Empty;

    public ReasoningOptions? Reasoning { get; init; }

    // Opaque JSON object forwarded verbatim as the request body's "provider"
    // field by routers that read it (OpenRouter). Empty when unset.
    public string ProviderPreferences { get; init; } = string.Empty;

    public bool IncludeRouterMetadata { get; init; }
}
