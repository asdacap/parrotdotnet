using Parrot.Llm;

namespace Parrot.Store;

internal sealed record CompactionGroupArtifactToolCall(string Id, string Name, string ArgumentsJson)
{
    public static CompactionGroupArtifactToolCall From(LLMToolCall toolCall) =>
        new(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson);
}
