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

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var match = Entries.LastOrDefault(entry =>
            string.Equals(entry.Path, canonicalPath, comparison));
        return match is null || match.Enabled;
    }
}
