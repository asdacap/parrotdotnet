namespace Parrot.Tools.ApplyPatch;

internal sealed class PatchMutation(
    PatchOperation operation,
    ToolMutationPath path,
    byte[]? before,
    byte[]? after,
    IReadOnlyList<PatchMatchReport> matchReports)
{
    public PatchOperation Operation { get; } = operation;

    public FileDiff.FileChange Change { get; } = new(path.Display, before, after);

    public byte[]? After { get; } = after;

    public IReadOnlyList<PatchMatchReport> MatchReports { get; } = matchReports;
}
