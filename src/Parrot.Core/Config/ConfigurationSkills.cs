using YamlDotNet.RepresentationModel;

namespace Parrot.Config;

internal sealed partial class Configuration
{
    public void SetSkillEnabled(string skillPath, bool enabled)
    {
        var canonical = CanonicalPath(skillPath, $"{SkillsKey}.entries.path");
        lock (_writeLock)
        {
            var root = LoadRoot(path);
            var skills = Mapping(root, SkillsKey);
            var entries = Child(skills, "entries", out var entriesNode)
                ? entriesNode as YamlSequenceNode ?? throw new InvalidDataException($"{SkillsKey}.entries must be a sequence")
                : [];
            for (var index = entries.Children.Count - 1; index >= 0; index--)
            {
                if (entries.Children[index] is YamlMappingNode itemMapping
                    && Child(itemMapping, "path", out var itemPath)
                    && itemPath is YamlScalarNode { Value: { } value }
                    && PathsEqual(value, canonical))
                {
                    entries.Children.RemoveAt(index);
                }
            }

            var entry = new YamlMappingNode
            {
                { "path", canonical },
                { "enabled", enabled ? "true" : "false" },
            };
            entries.Add(entry);
            skills.Children[new YamlScalarNode("entries")] = entries;

            var serialized = Serialize(root);
            var validatedRoot = LoadRootContent(serialized);
            var validatedSkills = ReadSkills(validatedRoot);
            WriteSerialized(serialized);
            Skills = validatedSkills;
            SkillsGeneration++;
        }
    }

    private static SkillConfiguration ReadSkills(YamlMappingNode root)
    {
        if (!Child(root, SkillsKey, out var node))
        {
            return SkillConfiguration.Default;
        }

        if (node is not YamlMappingNode skills)
        {
            throw new InvalidDataException($"{SkillsKey} must be a mapping");
        }

        ValidateKeys(skills, SkillsKey, "enabled", "entries");
        var enabled = !Child(skills, "enabled", out var enabledNode)
            || ParseBoolean(enabledNode, $"{SkillsKey}.enabled");
        var entries = new List<SkillConfigEntry>();
        if (Child(skills, "entries", out var entriesNode))
        {
            if (entriesNode is not YamlSequenceNode sequence)
            {
                throw new InvalidDataException($"{SkillsKey}.entries must be a sequence");
            }

            foreach (var (entry, index) in sequence.Children.Select((item, index) => (item, index)))
            {
                if (entry is not YamlMappingNode mapping)
                {
                    throw new InvalidDataException($"{SkillsKey}.entries[{index}] must be a mapping");
                }

                ValidateKeys(mapping, $"{SkillsKey}.entries[{index}]", "path", "enabled");
                var entryPath = NonEmptyScalar(mapping, "path", $"{SkillsKey}.entries[{index}].path");
                var canonical = CanonicalPath(entryPath, $"{SkillsKey}.entries[{index}].path");
                var entryEnabled = ReadBoolean(mapping, "enabled", $"{SkillsKey}.entries[{index}].enabled");
                _ = entries.RemoveAll(existing => PathsEqual(existing.Path, canonical));
                entries.Add(new SkillConfigEntry(canonical, entryEnabled));
            }
        }

        return new SkillConfiguration(enabled, entries);
    }

    private static bool PathsEqual(string configured, string canonical)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(CanonicalPath(configured, $"{SkillsKey}.entries.path"), canonical, comparison);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string CanonicalPath(string value, string field)
    {
        if (!Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException($"{field} must be an absolute path");
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(PlatformPath.Normalize(value));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"{field} must be a valid absolute path", exception);
        }
    }

    private void WriteSerialized(string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            _ = LoadRoot(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
