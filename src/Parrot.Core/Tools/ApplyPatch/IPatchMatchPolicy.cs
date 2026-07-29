namespace Parrot.Tools.ApplyPatch;

internal interface IPatchMatchPolicy
{
    int? ReportOrder { get; }

    int AdvanceSearch(int index, int patternLength, bool matched);

    IReadOnlyList<int> SelectMatches(IReadOnlyList<int> matches, string expectedDescription);
}
