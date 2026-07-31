namespace Parrot.Llm;

// One conversation message. The shape carries what each role needs and nothing
// more: an assistant message may carry ToolCalls, a tool message carries the
// ToolCallId it answers. Empty is absent -- never null (PARROT0003).
internal sealed record LLMMessage
{
    public required LLMRole Role { get; init; }

    public IReadOnlyList<LLMContent> Contents { get; init; } = [];

    public string Content => string.Concat(Contents.Where(content => content.Kind == LLMContentKind.Text).Select(content => content.Text));

    // Assistant only: the calls the model asked for.
    public IReadOnlyList<LLMToolCall> ToolCalls { get; init; } = [];

    // Tool only: which call this message is the result of.
    public string ToolCallId { get; init; } = string.Empty;

    public static LLMMessage User(string content) => User([LLMContent.TextPart(content)]);

    public static LLMMessage User(IReadOnlyList<LLMContent> contents) =>
        new() { Role = LLMRole.User, Contents = contents };

    public static LLMMessage System(string content) =>
        new() { Role = LLMRole.System, Contents = [LLMContent.TextPart(content)] };

    public static LLMMessage Assistant(string content, IReadOnlyList<LLMToolCall> toolCalls) =>
        new() { Role = LLMRole.Assistant, Contents = [LLMContent.TextPart(content)], ToolCalls = toolCalls };

    public static LLMMessage ToolResult(string toolCallId, string content) =>
        new() { Role = LLMRole.Tool, ToolCallId = toolCallId, Contents = [LLMContent.TextPart(content)] };
}
