using Parrot.Context;
using YamlDotNet.RepresentationModel;

namespace Parrot.Config;

internal sealed partial class Configuration
{
    public ContextSize? ContextLimit { get; private set; }

    internal ModelPresetContextLimits CaptureContextLimits() => new(
        ContextLimit,
        ModelAliases.SelectMany(alias => alias.Value.ContextLimit is { } limit
            ? new[] { new KeyValuePair<string, ContextSize>(alias.Key, limit) }
            : []));

    internal void SetContextLimit(ContextSize contextLimit)
    {
        ArgumentNullException.ThrowIfNull(contextLimit);
        lock (_writeLock)
        {
            var root = LoadRoot(path);
            root.Children[new YamlScalarNode("context_limit")] = new YamlScalarNode(contextLimit.ToString());
            Write(root);
            ContextLimit = contextLimit;
        }
    }

    private static ContextSize? ReadContextLimit(YamlMappingNode fields, string key, string fieldPath)
    {
        if (!Child(fields, key, out var node) || node is YamlScalarNode { Value: null or "null" or "~" })
        {
            return null;
        }

        if (node is not YamlScalarNode { Value: { } value })
        {
            throw new InvalidDataException($"{fieldPath} must be a context size or null");
        }

        try
        {
            return ContextSize.Parse(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"{fieldPath}: {exception.Message}", exception);
        }
    }

    private static ModelPresetContextLimits? ReadContextLimits(YamlMappingNode fields, string fieldPath)
    {
        if (!Child(fields, "context_limits", out var node))
        {
            return null;
        }

        if (node is not YamlMappingNode limits)
        {
            throw new InvalidDataException($"{fieldPath} must be a mapping");
        }

        ValidateKeys(limits, fieldPath, "default", ModelAliasesKey);
        var aliases = new SortedDictionary<string, ContextSize>(StringComparer.Ordinal);
        if (Child(limits, ModelAliasesKey, out var aliasesNode))
        {
            if (aliasesNode is not YamlMappingNode aliasLimits)
            {
                throw new InvalidDataException($"{fieldPath}.{ModelAliasesKey} must be a mapping");
            }

            foreach (var key in aliasLimits.Children.Keys)
            {
                if (key is not YamlScalarNode { Value: { } name })
                {
                    throw new InvalidDataException($"{fieldPath}.{ModelAliasesKey} keys must be strings");
                }

                ValidateAliasName(name);
                var limit = ReadContextLimit(aliasLimits, name, $"{fieldPath}.{ModelAliasesKey}.{name}");
                if (limit is not null)
                {
                    aliases.Add(name, limit);
                }
            }
        }

        return new(ReadContextLimit(limits, "default", $"{fieldPath}.default"), aliases);
    }

    private static YamlMappingNode WriteContextLimits(ModelPresetContextLimits limits)
    {
        var result = new YamlMappingNode();
        if (limits.Default is { } defaultLimit)
        {
            result.Add("default", defaultLimit.ToString());
        }

        var aliases = new YamlMappingNode();
        foreach (var alias in limits.ModelAliases)
        {
            aliases.Add(alias.Key, alias.Value.ToString());
        }

        result.Add(ModelAliasesKey, aliases);
        return result;
    }

    private static void ApplyContextLimits(
        YamlMappingNode root,
        SortedDictionary<string, ModelAliasConfig> aliases,
        ModelPresetContextLimits limits)
    {
        foreach (var name in limits.ModelAliases.Keys)
        {
            if (!aliases.ContainsKey(name))
            {
                throw new InvalidDataException($"model alias \"{name}\" is not defined");
            }
        }

        root.Children[new YamlScalarNode("context_limit")] = new YamlScalarNode(limits.Default?.ToString() ?? "null");
        var aliasFields = Mapping(root, ModelAliasesKey);
        foreach (var name in aliases.Keys.ToArray())
        {
            var limit = limits.ModelAliases.GetValueOrDefault(name);
            aliases[name] = aliases[name] with { ContextLimit = limit };
            Mapping(aliasFields, name).Children[new YamlScalarNode("context_limit")] =
                new YamlScalarNode(limit?.ToString() ?? "null");
        }
    }
}
