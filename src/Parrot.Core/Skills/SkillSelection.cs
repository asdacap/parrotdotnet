namespace Parrot.Skills;

internal sealed class SkillSelection
{
    public static IReadOnlyList<SkillMetadata> Select(
        IReadOnlyList<SkillMetadata> skills,
        IReadOnlyList<SkillMention> mentions)
    {
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(mentions);

        var selected = new List<SkillMetadata>();
        var paths = new HashSet<string>(PlatformPath.Comparer);
        foreach (var mention in mentions.OrderBy(item => item.Position))
        {
            var match = mention.Path is null
                ? skills.FirstOrDefault(skill =>
                    skill.Enabled && string.Equals(skill.Name, mention.Name, StringComparison.Ordinal))
                : skills.FirstOrDefault(skill =>
                    skill.Enabled
                    && string.Equals(skill.Name, mention.Name, StringComparison.Ordinal)
                    && (PathsEqual(skill.Path, mention.Path) || PathsEqual(skill.DiscoveryPath, mention.Path)));
            if (match is not null && paths.Add(match.Path))
            {
                selected.Add(match);
            }
        }

        return selected;
    }

    private static bool PathsEqual(string candidate, string requested)
    {
        try
        {
            return string.Equals(PlatformPath.Normalize(candidate), PlatformPath.Normalize(requested), PlatformPath.Comparison);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
