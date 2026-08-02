using Parrot.Llm;

namespace Parrot.Context;

internal sealed record CompactionResult(
    IReadOnlyList<LLMMessage> History,
    LLMMessage Summary,
    int RetainedDurableMessageCount,
    long Watermark);
