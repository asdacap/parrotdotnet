using Parrot.Llm;

namespace Parrot.Store;

internal sealed record ConversationItem(
    long Sequence,
    ConversationOrigin Origin,
    LLMRole Role,
    IReadOnlyList<ConversationPart> Parts,
    IReadOnlyList<LLMToolCall> ToolCalls,
    string ToolCallId);
