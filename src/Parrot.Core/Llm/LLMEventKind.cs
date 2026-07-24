namespace Parrot.Llm;

public enum LLMEventKind
{
    TextDelta,
    ReasoningDelta,
    ToolCallDelta,
    Retry,
}
