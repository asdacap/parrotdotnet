namespace Parrot.Llm;

// The final, durable state of one call. Live deltas went to the sink and are
// disposable; this is what survives (principle 10).
internal sealed record LLMResult
{
    public required string Text { get; init; }

    public string Reasoning { get; init; } = string.Empty;

    public string FinishReason { get; init; } = string.Empty;

    public LLMUsage Usage { get; init; } = LLMUsage.None;
}
