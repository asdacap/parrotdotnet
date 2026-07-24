namespace Parrot.Llm;

internal sealed record LLMUsage(int InputTokens, int OutputTokens)
{
    public static LLMUsage None { get; } = new(0, 0);
}
