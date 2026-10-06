using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Parrot.Config;

internal static class PredefinedConfigurationComposer
{
    internal static string Compose(IEnumerable<(string Name, string? Content)> sources)
    {
        var combined = new YamlMappingNode();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var templateOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var combinedTemplates = new YamlMappingNode();
        foreach (var (name, content) in sources)
        {
            if (content is null)
            {
                throw new InvalidDataException($"predefined configuration resource \"{name}\" was not embedded in this build");
            }

            var root = Parse(name, content);
            ValidateKeys(root, name, string.Empty);
            foreach (var entry in root.Children)
            {
                var key = ReadKey(entry.Key, name, string.Empty);
                if (key == "prompt_templates")
                {
                    if (entry.Value is not YamlMappingNode templates)
                    {
                        throw new InvalidDataException($"{name}: prompt_templates must be a mapping");
                    }

                    _ = combined.Children.TryAdd(entry.Key, combinedTemplates);
                    foreach (var template in templates.Children)
                    {
                        var id = ReadKey(template.Key, name, key);
                        AddOwner(templateOwners, id, name, $"{key}.{id}");
                        combinedTemplates.Add(template.Key, template.Value);
                    }
                }
                else
                {
                    AddOwner(owners, key, name, key);
                    combined.Add(entry.Key, entry.Value);
                }
            }
        }

        using var writer = new StringWriter();
        new YamlStream(new YamlDocument(combined)).Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static YamlMappingNode Parse(string name, string content)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(content);
        try
        {
            stream.Load(reader);
        }
        catch (YamlException failure)
        {
            throw new InvalidDataException($"{name}: invalid predefined configuration YAML", failure);
        }

        return stream.Documents is [{ RootNode: YamlMappingNode root }]
            ? root
            : throw new InvalidDataException($"{name}: predefined configuration must contain exactly one mapping document");
    }

    private static void AddOwner(Dictionary<string, string> owners, string key, string name, string path)
    {
        if (!owners.TryAdd(key, name))
        {
            throw new InvalidDataException($"duplicate predefined configuration at {path} in \"{owners[key]}\" and \"{name}\"");
        }
    }

    private static string ReadKey(YamlNode node, string name, string path) =>
        node is YamlScalarNode { Value: { } key }
            ? key
            : throw new InvalidDataException($"{name}: configuration mapping keys at {path} must be strings");

    private static void ValidateKeys(YamlNode node, string name, string path)
    {
        if (node is YamlMappingNode mapping)
        {
            foreach (var entry in mapping.Children)
            {
                var key = ReadKey(entry.Key, name, path);
                ValidateKeys(entry.Value, name, string.IsNullOrEmpty(path) ? key : $"{path}.{key}");
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (var item in sequence.Children)
            {
                ValidateKeys(item, name, path);
            }
        }
    }
}
