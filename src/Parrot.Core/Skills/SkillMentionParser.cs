using System.Text.RegularExpressions;

namespace Parrot.Skills;

internal sealed class SkillMentionParser
{
    private static readonly Regex Mention = new(
        @"(?<![A-Za-z0-9_])(?:\[\$(?<linkedName>[^\]\r\n]+)\]\(\s*(?<path>[^)]+?)\s*\)|\$(?<name>[A-Za-z0-9_:-]+))(?![A-Za-z0-9_:-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> EnvironmentNames = new(StringComparer.Ordinal)
    {
        "HOME", "PATH", "USER", "SHELL", "PWD", "TMP", "TMPDIR", "TEMP", "LANG", "TERM", "XDG_CONFIG_HOME",
    };

    public static IReadOnlyList<SkillMention> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var mentions = new List<SkillMention>();
        foreach (Match match in Mention.Matches(text))
        {
            var name = match.Groups["linkedName"].Success
                ? match.Groups["linkedName"].Value.Trim()
                : match.Groups["name"].Value;
            var path = match.Groups["path"].Success ? match.Groups["path"].Value.Trim() : null;
            if (!EnvironmentNames.Contains(name) || path is not null)
            {
                mentions.Add(new(name, path, match.Index));
            }
        }

        return mentions;
    }
}
