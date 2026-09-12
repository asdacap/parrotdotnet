using Parrot.Llm;

namespace Parrot.Store;

internal sealed record CompactionGroupArtifactMessage(
    string Role,
    IReadOnlyList<CompactionGroupArtifactContent> Contents,
    IReadOnlyList<CompactionGroupArtifactToolCall> ToolCalls,
    string ToolCallId)
{
    public static CompactionGroupArtifactMessage From(LLMMessage message) =>
        new(
            message.Role.ToString(),
            [.. message.Contents.Select(CompactionGroupArtifactContent.From)],
            [.. message.ToolCalls.Select(CompactionGroupArtifactToolCall.From)],
            message.ToolCallId);
}
