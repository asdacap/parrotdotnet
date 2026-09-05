using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Parrot.Skills;

internal sealed class SkillFrontmatterParser
{
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SkillMetadata Parse(string path, string content, SkillScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
        {
            throw new SkillParseException(path, "SKILL.md must begin with YAML frontmatter.", null);
        }

        var closing = Array.FindIndex(lines, 1, line => string.Equals(line.Trim(), "---", StringComparison.Ordinal));
        if (closing is < 0 or 1)
        {
            throw new SkillParseException(path, "SKILL.md frontmatter is not closed or is empty.", null);
        }

        var yaml = string.Join('\n', lines[1..closing]);
        YamlMappingNode mapping;
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);
            mapping = stream.Documents is [{ RootNode: YamlMappingNode root }, ..]
                ? root
                : throw new InvalidDataException("frontmatter must be a mapping");
        }
        catch (Exception exception) when (exception is YamlException or InvalidDataException)
        {
            throw new SkillParseException(path, "SKILL.md frontmatter contains invalid YAML.", exception);
        }

        var directoryName = new DirectoryInfo(Path.GetDirectoryName(path) ?? string.Empty).Name;
        var configuredName = Sanitize(Scalar(mapping, "name"));
        var name = configuredName.Length == 0 ? directoryName : configuredName;
        var description = Sanitize(Scalar(mapping, "description"));
        var shortDescription = mapping.Children.TryGetValue(new YamlScalarNode("metadata"), out var metadataNode)
            && metadataNode is YamlMappingNode metadata
            ? Sanitize(Scalar(metadata, "short-description"))
            : null;

        if (name is { Length: 0 or > MaxNameLength })
        {
            throw new SkillParseException(path, "skill name must be between 1 and 64 characters.", null);
        }

        if (description is { Length: 0 or > MaxDescriptionLength })
        {
            throw new SkillParseException(path, "skill description must be between 1 and 1024 characters.", null);
        }

        return new SkillMetadata(
            name,
            description,
            Path.GetFullPath(path),
            scope,
            null,
            string.IsNullOrEmpty(shortDescription) ? null : shortDescription,
            true,
            true);
    }

    private static string? Scalar(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode { Value: { } value }
            ? value
            : null;

    private static string Sanitize(string? value) =>
        value is null ? string.Empty : Whitespace.Replace(value, " ").Trim();
}
