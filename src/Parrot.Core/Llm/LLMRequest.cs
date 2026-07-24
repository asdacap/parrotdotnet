namespace Parrot.Llm;

internal sealed record LLMRequest
{
    public required string Model { get; init; }

    public required IReadOnlyList<LLMMessage> Messages { get; init; }

    public IReadOnlyList<LLMToolDefinition> Tools { get; init; } = [];

    public int MaxTokens { get; init; }
}
