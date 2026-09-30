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
            if (stream.Documents is not [{ RootNode: YamlMappingNode root }, ..])
            {
                return new(null, null, null);
            }

            var interfaceMapping = Mapping(root, "interface");
            bool? allowImplicitInvocation = bool.TryParse(
                Scalar(Mapping(root, "policy"), "allow_implicit_invocation"),
                out var allowed)
                ? allowed
                : null;
            return new(
                Read(interfaceMapping, "display_name", 64),
                Read(interfaceMapping, "short_description", 1024),
                allowImplicitInvocation);
        }
        catch (YamlException)
        {
            return new(null, null, null);
        }
    }

    private static YamlMappingNode? Mapping(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node as YamlMappingNode : null;

    private static string? Scalar(YamlMappingNode? mapping, string key) =>
        mapping is not null
        && mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
        && node is YamlScalarNode { Value: { } value }
            ? value
            : null;

    private static string? Read(YamlMappingNode? mapping, string key, int maxLength)
    {
        if (Scalar(mapping, key) is not { } value)
        {
            return null;
        }

        var sanitized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return sanitized is { Length: > 0 } && sanitized.Length <= maxLength ? sanitized : null;
    }
}
