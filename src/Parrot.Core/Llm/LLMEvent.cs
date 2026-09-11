namespace Parrot.Llm;

internal sealed record LLMEvent
{
    public required LLMEventKind Kind { get; init; }

    // TextDelta and ReasoningDelta: the fragment.
    // ToolCallDelta: the fully accumulated arguments. Retry: why.
    public string Text { get; init; } = string.Empty;

    // ReasoningDelta only.
    public LLMReasoningKind ReasoningKind { get; init; }

    public string ReasoningPartId { get; init; } = string.Empty;

    public bool ReasoningCompleted { get; init; }

    // ToolCallDelta only.
    public string ToolCallId { get; init; } = string.Empty;

    public string ToolName { get; init; } = string.Empty;

    // Retry only.
    public int Attempt { get; init; }

    public TimeSpan RetryAfter { get; init; }

    // Completed only. The stream's last event is its durable outcome, which is
    // principle 10: the deltas before it were disposable, this is not.
    public string FinishReason { get; init; } = string.Empty;

    public int InputTokens { get; init; }

    public int CachedInputTokens { get; init; }

    public int OutputTokens { get; init; }

    // Completed only: the fully assembled tool calls, if the model asked for
    // any. The provider reassembles the streamed fragments so the session does
    // not have to.
    public IReadOnlyList<LLMToolCall> ToolCalls { get; init; } = [];

    public string AssistantText { get; init; } = string.Empty;

    public static LLMEvent HttpRequestStarted() => new() { Kind = LLMEventKind.HttpRequestStarted };

    public static LLMEvent HttpResponseHeadersReceived() => new() { Kind = LLMEventKind.HttpResponseHeadersReceived };

    public static LLMEvent TextDelta(string fragment) =>
        new() { Kind = LLMEventKind.TextDelta, Text = fragment };

    public static LLMEvent ReasoningDelta(string fragment) =>
        ReasoningDelta(fragment, LLMReasoningKind.Raw, string.Empty, completed: false);

    public static LLMEvent ReasoningDelta(
        string fragment,
        LLMReasoningKind reasoningKind,
        string partId,
        bool completed) =>
        new()
        {
            Kind = LLMEventKind.ReasoningDelta,
            Text = fragment,
            ReasoningKind = reasoningKind,
            ReasoningPartId = partId,
            ReasoningCompleted = completed,
        };

    public static LLMEvent ToolCallDelta(string toolCallId, string toolName, string arguments) =>
        new()
        {
            Kind = LLMEventKind.ToolCallDelta,
            ToolCallId = toolCallId,
            ToolName = toolName,
            Text = arguments,
        };

    public static LLMEvent Retry(int attempt, TimeSpan retryAfter, string reason) =>
        new() { Kind = LLMEventKind.Retry, Attempt = attempt, RetryAfter = retryAfter, Text = reason };

    public static LLMEvent Completed(
        string finishReason,
        int inputTokens,
        int cachedInputTokens,
        int outputTokens,
        string assistantText,
        IReadOnlyList<LLMToolCall> toolCalls) =>
        new()
        {
            Kind = LLMEventKind.Completed,
            FinishReason = finishReason,
            InputTokens = inputTokens,
            CachedInputTokens = cachedInputTokens,
            OutputTokens = outputTokens,
            AssistantText = assistantText,
            ToolCalls = toolCalls,
        };
}
