namespace Parrot.Config;

internal sealed record SkillConfiguration(bool Enabled, IReadOnlyList<SkillConfigEntry> Entries)
{
    public static SkillConfiguration Default { get; } = new(true, []);

    public bool IsEnabled(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        if (!Enabled)
        {
            return false;
        }

        var normalized = PlatformPath.Normalize(canonicalPath);
        var match = Entries.LastOrDefault(entry =>
            string.Equals(PlatformPath.Normalize(entry.Path), normalized, PlatformPath.Comparison));
        return match is null || match.Enabled;
    }
}
