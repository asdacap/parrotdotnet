using Parrot.Llm;

namespace Parrot.Context;

internal sealed record CompactionGroup(
    IReadOnlyList<LLMMessage> Messages,
    long EndWatermark,
    bool HasCheckpointBefore,
    bool IsComplete);
