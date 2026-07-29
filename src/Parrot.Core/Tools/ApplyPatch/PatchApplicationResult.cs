namespace Parrot.Tools.ApplyPatch;

internal sealed class PatchApplicationResult(byte[] content, IReadOnlyList<PatchMatchReport> matchReports)
{
    public byte[] Content { get; } = content;

    public IReadOnlyList<PatchMatchReport> MatchReports { get; } = matchReports;
}
