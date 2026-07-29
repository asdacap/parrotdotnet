namespace Parrot.Tools.ApplyPatch;

internal sealed class PatchReplacement(int start, int oldCount, List<PatchFileLine> lines)
{
    public int Start { get; } = start;

    public int OldCount { get; } = oldCount;

    public List<PatchFileLine> Lines { get; } = lines;

    public int Order { get; init; }
}
