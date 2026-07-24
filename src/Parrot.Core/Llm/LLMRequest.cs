namespace Parrot.Llm;

public sealed record LLMRequest
{
    public required string Model { get; init; }

    public required IReadOnlyList<LLMMessage> Messages { get; init; }

    public int MaxTokens { get; init; }
}
