namespace Parrot.Llm;

internal enum LLMEventKind
{
    TextDelta,
    ReasoningDelta,
    ToolCallDelta,
    Retry,
    Completed,
    HttpRequestStarted,
    HttpResponseHeadersReceived,
}
