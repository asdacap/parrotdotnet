namespace Parrot.Tools.ApplyPatch;

internal sealed class PatchHunk(IReadOnlyList<PatchLine> lines)
{
    public IReadOnlyList<PatchLine> Lines { get; } = lines;
}
