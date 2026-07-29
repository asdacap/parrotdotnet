namespace Parrot.Tools.ApplyPatch;

internal sealed class PatchHunk(IReadOnlyList<PatchLine> lines, IPatchMatchPolicy matchPolicy)
{
    public IReadOnlyList<PatchLine> Lines { get; } = lines;

    public IPatchMatchPolicy MatchPolicy { get; } = matchPolicy;
}
