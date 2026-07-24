namespace Parrot.Tools;

internal sealed class PatchHunk(IReadOnlyList<PatchLine> lines)
{
    public IReadOnlyList<PatchLine> Lines { get; } = lines;
}
