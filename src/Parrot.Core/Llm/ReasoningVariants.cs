namespace Parrot.Llm;

// Builds reasoning variants from a list of effort levels, ordered with the
// default effort first so a fresh selection lands on the fallback.
internal static class ReasoningVariants
{
    public static IReadOnlyList<ModelVariant> FromEfforts(IReadOnlyList<string> efforts, string defaultEffort)
    {
        if (efforts.Count == 0)
        {
            return [];
        }

        var ordered = new List<string>(efforts);
        var index = ordered.IndexOf(defaultEffort);

        if (defaultEffort.Length > 0 && index > 0)
        {
            (ordered[0], ordered[index]) = (ordered[index], ordered[0]);
        }

        return [.. ordered.Select(effort => new ModelVariant(effort, effort))];
    }
}
