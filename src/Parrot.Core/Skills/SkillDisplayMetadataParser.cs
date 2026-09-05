using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Parrot.Skills;

internal static class SkillDisplayMetadataParser
{
    public static SkillDisplayMetadata Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(content);
            stream.Load(reader);
            if (stream.Documents is not [{ RootNode: YamlMappingNode root }, ..]
                || !root.Children.TryGetValue(new YamlScalarNode("interface"), out var interfaceNode)
                || interfaceNode is not YamlMappingNode interfaceMapping)
            {
                return new(null, null);
            }

            return new(
                Read(interfaceMapping, "display_name", 64),
                Read(interfaceMapping, "short_description", 1024));
        }
        catch (YamlException)
        {
            return new(null, null);
        }
    }

    private static string? Read(YamlMappingNode mapping, string key, int maxLength)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            || node is not YamlScalarNode { Value: { } value })
        {
            return null;
        }

        var sanitized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return sanitized is { Length: > 0 } && sanitized.Length <= maxLength ? sanitized : null;
    }
}
