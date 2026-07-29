namespace Parrot.Tools.ApplyPatch;

internal sealed class UnifiedPatchMatchPolicy : IPatchMatchPolicy
{
    public int? ReportOrder => null;

    public int AdvanceSearch(int index, int patternLength, bool matched) => index + 1;

    public IReadOnlyList<int> SelectMatches(IReadOnlyList<int> matches, string expectedDescription)
    {
        if (matches.Count > 1)
        {
            throw new PatchException($"Found {matches.Count} matches for {expectedDescription}; include more surrounding lines.");
        }

        return matches;
    }
}
