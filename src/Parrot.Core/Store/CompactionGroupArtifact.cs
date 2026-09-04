using Parrot.Context;

namespace Parrot.Store;

internal sealed record CompactionGroupArtifact(IReadOnlyList<CompactionGroupArtifactMessage> Messages)
{
    public static CompactionGroupArtifact From(CompactionGroup group) =>
        new([.. group.Messages.Select(CompactionGroupArtifactMessage.From)]);
}
