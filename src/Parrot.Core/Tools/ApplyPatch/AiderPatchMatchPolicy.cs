namespace Parrot.Tools.ApplyPatch;

internal sealed class AiderPatchMatchPolicy(int reportOrder) : IPatchMatchPolicy
{
    public int? ReportOrder { get; } = reportOrder;

    public int AdvanceSearch(int index, int patternLength, bool matched) =>
        matched ? index + patternLength : index + 1;

    public IReadOnlyList<int> SelectMatches(IReadOnlyList<int> matches, string expectedDescription) => matches;
}
