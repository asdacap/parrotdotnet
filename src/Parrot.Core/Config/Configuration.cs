using System.Globalization;
using Parrot.Security;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Parrot.Config;

// The user-owned config.yaml is layered over a generated, agent-readable copy
// of the predefined defaults. Writes intentionally touch only the user layer.
internal sealed class Configuration(string path)
{
    private const string ModelAliasesKey = "model_aliases";
    private const string ModelAugmentSystemPromptsKey = "model_augment_system_prompts";
    private const string ModelKey = "model";
    private const string PromptKey = "prompt";
    private const string DefaultProfileKey = "default_profile";
    private const string DisabledToolsKey = "disabled_tools";
    private const string CliUtilitiesKey = "cli_utilities";

    private readonly Lock _writeLock = new();

    // A read-modify-write. Rename gives atomicity, not serialisation across
    // hosts, so this is last-write-wins for a global preference -- which is
    // what upstream accepts for the model too.
    public string Model { get; private set; } = string.Empty;

    public string Prompt { get; private set; } = string.Empty;

    public bool InlineDiff { get; private set; } = true;

    // The configured providers, keyed by id. Empty is the common case: the
    // preset providers need only a credential, not a config entry.
    public IReadOnlyDictionary<string, ProviderConfig> Providers { get; private set; } =
        new Dictionary<string, ProviderConfig>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ModelAliasConfig> ModelAliases { get; private set; } =
        new SortedDictionary<string, ModelAliasConfig>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> ModelAugmentSystemPrompts { get; private set; } =
        new SortedDictionary<string, string>(StringComparer.Ordinal);

    public WebFetchConfig WebFetch { get; private set; } = new();

    public IReadOnlyList<SandboxRule> SandboxRules { get; private set; } = [];

    public IReadOnlySet<string> DisabledTools { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ProfileConfig> Profiles { get; private set; } =
        new Dictionary<string, ProfileConfig>(StringComparer.Ordinal);

    public string DefaultProfile { get; private set; } = string.Empty;

    public CliUtilityCandidates CliUtilities { get; private set; } = new([], []);

    public static Configuration Load(string path, string predefinedPath)
    {
        CopyPredefined(predefinedPath);
        var root = Merge(LoadRoot(predefinedPath), LoadRoot(path));

        return new(path)
        {
            Model = Scalar(root, ModelKey),
            Prompt = NonEmptyScalar(root, PromptKey, PromptKey),
            InlineDiff = ReadInlineDiff(root),
            ModelAliases = ReadModelAliases(root),
            ModelAugmentSystemPrompts = ReadModelAugmentSystemPrompts(root),
            Providers = ReadProviders(root),
            WebFetch = ReadWebFetch(root),
            SandboxRules = ReadSandboxRules(root, "sandbox_rules"),
            DisabledTools = ReadDisabledTools(root),
            Profiles = ReadProfiles(root),
            DefaultProfile = ReadDefaultProfile(root),
            CliUtilities = ReadCliUtilities(root),
        };
    }

    // Only the interactive /model reaches here; --model is a per-invocation
    // override that does not persist.
    public void SetModel(string model)
    {
        lock (_writeLock)
        {
            var root = LoadRoot(path);
            root.Children[new YamlScalarNode(ModelKey)] = new YamlScalarNode(model);
            Write(root);
            Model = model;
        }
    }

    public void SetModelAlias(string name, string target)
    {
        lock (_writeLock)
        {
            if (!ModelAliases.TryGetValue(name, out var existing))
            {
                throw new InvalidDataException($"model alias \"{name}\" is not defined");
            }

            ValidateAliasName(name);
            ValidateModelSelector($"{ModelAliasesKey}.{name}.model_string", target, allowEmpty: true);

            var root = LoadRoot(path);
            var aliases = Mapping(root, ModelAliasesKey);
            var alias = Mapping(aliases, name);
            alias.Children[new YamlScalarNode("model_string")] = new YamlScalarNode(target);
            Write(root);

            var updated = new SortedDictionary<string, ModelAliasConfig>(
                ModelAliases.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                StringComparer.Ordinal)
            {
                [name] = existing with { ModelString = target },
            };
            ModelAliases = updated;
        }
    }

    private static void CopyPredefined(string destination)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Config", "predefined_config.yaml");
        var directory = Path.GetDirectoryName(destination);

        ArgumentException.ThrowIfNullOrEmpty(destination);
        ArgumentException.ThrowIfNullOrEmpty(source);

        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            var temporary = Path.Combine(directory ?? string.Empty, Path.GetRandomFileName());
            File.Copy(source, temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("unable to write predefined configuration", failure);
        }
    }

    private static YamlMappingNode Merge(YamlMappingNode defaults, YamlMappingNode overrides)
    {
        foreach (var entry in overrides.Children)
        {
            if (defaults.Children.TryGetValue(entry.Key, out var current) &&
                current is YamlMappingNode baseMapping && entry.Value is YamlMappingNode overrideMapping)
            {
                _ = Merge(baseMapping, overrideMapping);
            }
            else
            {
                defaults.Children[Clone(entry.Key)] = Clone(entry.Value);
            }
        }

        return defaults;
    }

    private static YamlNode Clone(YamlNode node) => node switch
    {
        YamlMappingNode mapping => CloneMapping(mapping),
        YamlSequenceNode sequence => CloneSequence(sequence),
        YamlScalarNode scalar => new YamlScalarNode(scalar.Value) { Style = scalar.Style },
        _ => throw new InvalidDataException("configuration contains an unsupported YAML node"),
    };

    private static YamlMappingNode CloneMapping(YamlMappingNode source)
    {
        var clone = new YamlMappingNode();

        foreach (var entry in source.Children)
        {
            clone.Add(Clone(entry.Key), Clone(entry.Value));
        }

        return clone;
    }

    private static YamlSequenceNode CloneSequence(YamlSequenceNode source)
    {
        var clone = new YamlSequenceNode();

        foreach (var item in source.Children)
        {
            clone.Add(Clone(item));
        }

        return clone;
    }

    private static string Serialize(YamlMappingNode root)
    {
        var stream = new YamlStream(new YamlDocument(root));

        using var buffer = new StringWriter();
        stream.Save(buffer, assignAnchors: false);

        // YamlStream.Save appends an explicit document-end marker; a config file
        // reads cleaner without it, and it round-trips either way.
        var text = buffer.ToString().TrimEnd();

        if (text.EndsWith("...", StringComparison.Ordinal))
        {
            text = text[..^3].TrimEnd();
        }

        return text + "\n";
    }

    private static SortedDictionary<string, ModelAliasConfig> ReadModelAliases(YamlMappingNode root)
    {
        var aliases = new SortedDictionary<string, ModelAliasConfig>(StringComparer.Ordinal);

        if (!Child(root, ModelAliasesKey, out var node))
        {
            return aliases;
        }

        if (node is not YamlMappingNode configured)
        {
            throw new InvalidDataException($"{ModelAliasesKey} must be a mapping");
        }

        foreach (var entry in configured.Children)
        {
            if (entry.Key is not YamlScalarNode { Value: { } name })
            {
                throw new InvalidDataException($"{ModelAliasesKey} keys must be strings");
            }

            ValidateAliasName(name);

            if (entry.Value is not YamlMappingNode fields)
            {
                throw new InvalidDataException($"{ModelAliasesKey}.{name} must be a mapping");
            }

            ValidateAliasKeys(fields, name);
            _ = aliases.TryGetValue(name, out var inherited);
            var modelString = Child(fields, "model_string", out _)
                ? ScalarValue(fields, "model_string", $"{ModelAliasesKey}.{name}.model_string")
                : inherited?.ModelString ?? string.Empty;
            var usage = Child(fields, "usage", out _)
                ? ScalarValue(fields, "usage", $"{ModelAliasesKey}.{name}.usage")
                : inherited?.Usage ?? string.Empty;
            string? augmentation;

            if (Child(fields, "augment_system_prompt", out var augmentationNode))
            {
                augmentation = augmentationNode switch
                {
                    YamlScalarNode { Value: "null" } => null,
                    YamlScalarNode scalar => scalar.Value,
                    _ => throw new InvalidDataException(
                        $"{ModelAliasesKey}.{name}.augment_system_prompt must be a string or null"),
                };
            }
            else
            {
                augmentation = inherited?.AugmentSystemPrompt;
            }

            if (usage.Length == 0)
            {
                throw new InvalidDataException($"{ModelAliasesKey}.{name}.usage must not be empty");
            }

            var icon = ReadModelAliasIcon(fields, name);

            ValidateModelSelector($"{ModelAliasesKey}.{name}.model_string", modelString, allowEmpty: true);
            aliases[name] = new(modelString, usage, augmentation, icon);
        }

        return aliases;
    }

    private static SortedDictionary<string, string> ReadModelAugmentSystemPrompts(YamlMappingNode root)
    {
        var prompts = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (!Child(root, ModelAugmentSystemPromptsKey, out var node))
        {
            return prompts;
        }

        if (node is not YamlMappingNode configured)
        {
            throw new InvalidDataException($"{ModelAugmentSystemPromptsKey} must be a mapping");
        }

        foreach (var entry in configured.Children)
        {
            if (entry.Key is not YamlScalarNode { Value: { } selector } ||
                entry.Value is not YamlScalarNode { Value: { } prompt })
            {
                throw new InvalidDataException($"{ModelAugmentSystemPromptsKey} must contain string values");
            }

            ValidateModelSelector($"{ModelAugmentSystemPromptsKey} key", selector, allowEmpty: false);
            prompts[selector] = prompt;
        }

        return prompts;
    }

    private static ModelAliasIconConfig? ReadModelAliasIcon(YamlMappingNode fields, string name)
    {
        var path = $"{ModelAliasesKey}.{name}.icon";
        if (!Child(fields, "icon", out var node) ||
            node is YamlScalarNode { Value: null or "" or "null" or "Null" or "NULL" or "~" })
        {
            return null;
        }

        if (node is not YamlMappingNode icon)
        {
            throw new InvalidDataException($"{path} must be a glyph and color mapping, null, or empty");
        }

        ValidateKeys(icon, path, "glyph", "color");
        var glyph = ScalarValue(icon, "glyph", $"{path}.glyph");
        var color = ScalarValue(icon, "color", $"{path}.color");
        if (color is not ("black" or "red" or "green" or "yellow" or "blue" or "magenta" or "cyan" or "white" or "gray"))
        {
            throw new InvalidDataException($"{path}.color must be a supported color");
        }

        if (glyph.Length == 0)
        {
            return null;
        }

        var graphemes = StringInfo.GetTextElementEnumerator(glyph);
        var count = 0;
        while (graphemes.MoveNext())
        {
            count++;
        }

        var visible = false;
        for (var index = 0; index < glyph.Length; index += char.IsSurrogatePair(glyph, index) ? 2 : 1)
        {
            var category = char.GetUnicodeCategory(glyph, index);
            visible |= category is not (
                UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpaceSeparator or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate);
        }

        if (count != 1 || !visible || glyph.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new InvalidDataException($"{path}.glyph must be one visible grapheme");
        }

        return new(glyph, color);
    }

    private static void ValidateAliasKeys(YamlMappingNode fields, string name)
    {
        foreach (var key in fields.Children.Keys)
        {
            if (key is not YamlScalarNode { Value: { } value } ||
                value is not ("model_string" or "usage" or "augment_system_prompt" or "icon"))
            {
                throw new InvalidDataException($"{ModelAliasesKey}.{name} contains an unsupported key");
            }
        }
    }

    private static void ValidateAliasName(string name)
    {
        if (name.Length == 0)
        {
            throw new InvalidDataException($"{ModelAliasesKey} key must not be empty");
        }

        if (!string.Equals(name.Trim(), name, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"model alias name \"{name}\" must not have surrounding whitespace");
        }

        if (name.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"model alias name \"{name}\" must not contain '/'");
        }
    }

    private static void ValidateModelSelector(string field, string selector, bool allowEmpty)
    {
        if (selector.Length == 0 && allowEmpty)
        {
            return;
        }

        if (!string.Equals(selector.Trim(), selector, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{field} must not have surrounding whitespace");
        }

        if (selector.Any(character => char.IsControl(character) && char.IsWhiteSpace(character)))
        {
            throw new InvalidDataException($"{field} must not contain control whitespace");
        }

        if (selector.Split('/').Length < 2)
        {
            throw new InvalidDataException($"{field} must be provider/model");
        }

        if (selector.Split('/').Any(segment => segment.Length == 0))
        {
            throw new InvalidDataException($"{field} must not contain empty path segments");
        }
    }

    private static string ScalarValue(YamlMappingNode parent, string key, string field) =>
        Child(parent, key, out var node) && node is YamlScalarNode { Value: { } scalar }
            ? scalar
            : throw new InvalidDataException($"{field} must be a string");

    private static YamlMappingNode Mapping(YamlMappingNode parent, string key)
    {
        if (!Child(parent, key, out var node))
        {
            var created = new YamlMappingNode();
            parent.Children[new YamlScalarNode(key)] = created;
            return created;
        }

        return node as YamlMappingNode ?? throw new InvalidDataException($"{key} must be a mapping");
    }

    private static Dictionary<string, ProfileConfig> ReadProfiles(YamlMappingNode root)
    {
        var result = new Dictionary<string, ProfileConfig>(StringComparer.Ordinal);
        var ids = new[] { "build", "plan", "query", "explorer", "review", "worker", "thinker" };

        if (!Child(root, "profiles", out var node) || node is not YamlMappingNode profiles)
        {
            throw new InvalidDataException("profiles must be a mapping");
        }

        foreach (var entry in profiles.Children)
        {
            if (entry.Key is not YamlScalarNode { Value: { Length: > 0 } id } ||
                entry.Value is not YamlMappingNode profile)
            {
                throw new InvalidDataException("each profile must be a named mapping");
            }

            if (!ids.Contains(id, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"profiles.{id} is not supported");
            }

            _ = profile.Children.Remove(new YamlScalarNode("status"));
            ValidateKeys(profile, $"profiles.{id}", "prompt", "usage", "hard_rules", "allowed_tools", "max_turns", "recursion_limit", "read_only", "is_user_agent", "sandbox_rules");
        }

        foreach (var id in ids)
        {
            if (!Child(profiles, id, out var nodeForProfile) || nodeForProfile is not YamlMappingNode profile)
            {
                throw new InvalidDataException($"profiles.{id} must be a mapping");
            }

            result[id] = new ProfileConfig(
                NonEmptyScalar(profile, "prompt", $"profiles.{id}.prompt"),
                NonEmptyScalar(profile, "usage", $"profiles.{id}.usage"),
                NonEmptyStrings(profile, "hard_rules", $"profiles.{id}.hard_rules"),
                ReadAllowedTools(profile, $"profiles.{id}.allowed_tools"),
                PositiveInteger(profile, "max_turns", $"profiles.{id}.max_turns"),
                NonNegativeInteger(profile, "recursion_limit", $"profiles.{id}.recursion_limit"),
                ReadBoolean(profile, "read_only", $"profiles.{id}.read_only"),
                ReadBoolean(profile, "is_user_agent", $"profiles.{id}.is_user_agent"),
                ReadSandboxRules(profile, $"profiles.{id}.sandbox_rules"));
        }

        return result;
    }

    private static string ReadDefaultProfile(YamlMappingNode root)
    {
        var selected = NonEmptyScalar(root, DefaultProfileKey, DefaultProfileKey);
        var profiles = ReadProfiles(root);

        if (!profiles.TryGetValue(selected, out var profile))
        {
            throw new InvalidDataException($"{DefaultProfileKey} {selected} is not configured in profiles");
        }

        if (!profile.IsUserAgent)
        {
            throw new InvalidDataException($"{DefaultProfileKey} {selected} is not a user agent profile");
        }

        return selected;
    }

    private static List<string> NonEmptyStrings(YamlMappingNode parent, string key, string path)
    {
        if (!Child(parent, key, out var node) || node is not YamlSequenceNode sequence || sequence.Children.Count == 0)
        {
            throw new InvalidDataException($"{path} must be a non-empty string sequence");
        }

        var values = new List<string>(sequence.Children.Count);
        foreach (var item in sequence.Children)
        {
            if (item is not YamlScalarNode { Value: { } value } || string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException($"{path} must be a non-empty string sequence");
            }

            values.Add(value);
        }

        return values;
    }

    private static CliUtilityCandidates ReadCliUtilities(YamlMappingNode root)
    {
        if (!Child(root, CliUtilitiesKey, out var node) || node is not YamlMappingNode utilities)
        {
            throw new InvalidDataException($"{CliUtilitiesKey} must be a mapping");
        }

        ValidateKeys(utilities, CliUtilitiesKey, "expected", "optional");
        var expected = ReadCliUtilityNames(utilities, "expected");
        var optional = ReadCliUtilityNames(utilities, "optional");
        var expectedNames = expected.ToHashSet(StringComparer.Ordinal);

        return new(expected, [.. optional.Where(name => !expectedNames.Contains(name))]);
    }

    private static List<string> ReadCliUtilityNames(YamlMappingNode utilities, string key)
    {
        var path = $"{CliUtilitiesKey}.{key}";
        if (!Child(utilities, key, out var node) || node is not YamlSequenceNode sequence)
        {
            throw new InvalidDataException($"{path} must be a string sequence");
        }

        var names = new List<string>(sequence.Children.Count);
        foreach (var item in sequence.Children)
        {
            if (item is not YamlScalarNode { Value: { } name } || name.Length == 0 ||
                name.Any(char.IsWhiteSpace) || name.Contains('/', StringComparison.Ordinal) ||
                name.Contains('\\', StringComparison.Ordinal) || names.Contains(name, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"{path} must contain unique nonempty utility names without whitespace or path separators");
            }

            names.Add(name);
        }

        return names;
    }

    private static List<string>? ReadAllowedTools(YamlMappingNode parent, string path)
    {
        if (!Child(parent, "allowed_tools", out var node) || node is YamlScalarNode { Value: "null" })
        {
            return null;
        }

        if (node is not YamlSequenceNode sequence)
        {
            throw new InvalidDataException($"{path} must be a string sequence or null");
        }

        var values = new List<string>(sequence.Children.Count);
        foreach (var item in sequence.Children)
        {
            if (item is not YamlScalarNode { Value: { } value } || value.Length == 0 ||
                !string.Equals(value.Trim(), value, StringComparison.Ordinal) || values.Contains(value, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"{path} must contain unique nonblank untrimmed tool names");
            }

            values.Add(value);
        }

        return values;
    }

    private static HashSet<string> ReadDisabledTools(YamlMappingNode root)
    {
        if (!Child(root, DisabledToolsKey, out var node))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        if (node is not YamlMappingNode configured)
        {
            throw new InvalidDataException($"{DisabledToolsKey} must be a mapping");
        }

        var disabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in configured.Children)
        {
            if (entry.Key is not YamlScalarNode { Value: { } name } || name.Length == 0 ||
                !string.Equals(name.Trim(), name, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{DisabledToolsKey} keys must be nonblank trimmed tool names");
            }

            switch (entry.Value)
            {
                case YamlScalarNode { Value: "true" }:
                    _ = disabled.Add(name);
                    break;
                case YamlScalarNode { Value: "false" }:
                    break;
                default:
                    throw new InvalidDataException($"{DisabledToolsKey}.{name} must be true or false");
            }
        }

        return disabled;
    }

    private static List<SandboxRule> ReadSandboxRules(YamlMappingNode parent, string path)
    {
        if (!Child(parent, "sandbox_rules", out var node))
        {
            return [];
        }

        if (node is not YamlSequenceNode sequence)
        {
            throw new InvalidDataException($"{path} must be a sequence");
        }

        var result = new List<SandboxRule>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            if (sequence.Children[index] is not YamlMappingNode item)
            {
                throw new InvalidDataException($"{path}.{index} requires scalar path and rule fields");
            }

            ValidateKeys(item, $"{path}.{index}", "path", "rule");

            if (!Child(item, "path", out var pathNode) || pathNode is not YamlScalarNode { Value: { } rulePath } ||
                string.IsNullOrWhiteSpace(rulePath) || !Path.IsPathFullyQualified(rulePath) ||
                !Child(item, "rule", out var actionNode) || actionNode is not YamlScalarNode { Value: { } action })
            {
                throw new InvalidDataException($"{path}.{index} requires scalar path and rule fields");
            }

            result.Add(new(rulePath, ParseAction(action, $"{path}.{index}.rule")));
        }

        return result;
    }

    private static void ValidateKeys(YamlMappingNode mapping, string path, params string[] supportedKeys)
    {
        foreach (var key in mapping.Children.Keys)
        {
            if (key is not YamlScalarNode { Value: { } value } || !supportedKeys.Contains(value, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"{path} contains an unsupported key");
            }
        }
    }

    private static SandboxRuleAction ParseAction(string action, string path) => action switch
    {
        "allow_write" => SandboxRuleAction.AllowWrite,
        "deny_read" => SandboxRuleAction.DenyRead,
        "allow_read" => SandboxRuleAction.AllowRead,
        "deny_write" => SandboxRuleAction.DenyWrite,
        _ => throw new InvalidDataException($"{path} has invalid action {action}"),
    };

    private static bool ReadInlineDiff(YamlMappingNode root)
    {
        if (!Child(root, "inline_diff", out var node))
        {
            return true;
        }

        return node switch
        {
            YamlScalarNode { Value: "true" } => true,
            YamlScalarNode { Value: "false" } => false,
            _ => throw new InvalidDataException("inline_diff must be true or false"),
        };
    }

    private static string NonEmptyScalar(YamlMappingNode parent, string key, string path)
    {
        if (!Child(parent, key, out var node) || node is not YamlScalarNode { Value: { } value } scalar ||
            string.IsNullOrWhiteSpace(value) ||
            (scalar.Style == ScalarStyle.Plain && value is "null" or "Null" or "NULL" or "~"))
        {
            throw new InvalidDataException($"{path} must be a non-empty string");
        }

        return value;
    }

    private static int NonNegativeInteger(YamlMappingNode parent, string key, string path)
    {
        if (!Child(parent, key, out var node) || node is not YamlScalarNode { Value: { } value } ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            throw new InvalidDataException($"{path} must be a non-negative integer");
        }

        return parsed;
    }

    private static int PositiveInteger(YamlMappingNode parent, string key, string path)
    {
        if (!Child(parent, key, out var node) || node is not YamlScalarNode { Value: { } value } ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new InvalidDataException($"{path} must be a positive integer");
        }

        return parsed;
    }

    private static bool ReadBoolean(YamlMappingNode parent, string key, string path)
    {
        if (!Child(parent, key, out var node))
        {
            throw new InvalidDataException($"{path} must be true or false");
        }

        return node switch
        {
            YamlScalarNode { Value: "true" } => true,
            YamlScalarNode { Value: "false" } => false,
            _ => throw new InvalidDataException($"{path} must be true or false"),
        };
    }

    private static WebFetchConfig ReadWebFetch(YamlMappingNode root) =>
        Child(root, "web_fetch", out var node) && node is YamlMappingNode webFetch
            ? new() { AllowPrivate = Scalar(webFetch, "allow_private") == "true" }
            : new();

    private static Dictionary<string, ProviderConfig> ReadProviders(YamlMappingNode root)
    {
        var result = new Dictionary<string, ProviderConfig>(StringComparer.Ordinal);

        if (!Child(root, "providers", out var node) || node is not YamlMappingNode providers)
        {
            return result;
        }

        foreach (var entry in providers.Children)
        {
            if (entry.Key is not YamlScalarNode { Value: { } id } || entry.Value is not YamlMappingNode item)
            {
                continue;
            }

            result[id] = new ProviderConfig
            {
                Type = Scalar(item, "type"),
                Protocol = Scalar(item, "protocol"),
                BaseUrl = Scalar(item, "base_url"),
                ApiKeyEnv = Scalar(item, "api_key_env"),
                AllowInsecureLocalhost = Scalar(item, "allow_insecure_localhost") == "true",
                HeaderTimeoutMs = Integer(item, "header_timeout_ms"),
                Headers = StringMap(item, "headers"),
                ProviderPreferences = RawJson(item, "provider_preferences"),
                Models = ReadModels(item),
            };
        }

        return result;
    }

    private static Dictionary<string, ModelConfig> ReadModels(YamlMappingNode provider)
    {
        var result = new Dictionary<string, ModelConfig>(StringComparer.Ordinal);

        if (!Child(provider, "models", out var node) || node is not YamlMappingNode models)
        {
            return result;
        }

        foreach (var entry in models.Children)
        {
            if (entry.Key is not YamlScalarNode { Value: { } id } || entry.Value is not YamlMappingNode item)
            {
                continue;
            }

            result[id] = new ModelConfig
            {
                Name = Scalar(item, "name"),
                Context = Integer(item, "context") ?? 0,
                MaxTokens = Integer(item, "max_tokens") ?? 0,
                Tools = Scalar(item, "tools") == "true",
                Reasoning = Scalar(item, "reasoning") == "true",
                Output = StringSequence(item, "output"),
                Variants = ReadVariants(item),
            };
        }

        return result;
    }

    private static Dictionary<string, string> ReadVariants(YamlMappingNode model)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!Child(model, "variants", out var node) || node is not YamlMappingNode variants)
        {
            return result;
        }

        foreach (var entry in variants.Children)
        {
            if (entry.Key is YamlScalarNode { Value: { } name } && entry.Value is YamlMappingNode item)
            {
                result[name] = Scalar(item, "reasoning_effort");
            }
        }

        return result;
    }

    private static Dictionary<string, string> StringMap(YamlMappingNode parent, string key)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (Child(parent, key, out var node) && node is YamlMappingNode map)
        {
            foreach (var entry in map.Children)
            {
                if (entry.Key is YamlScalarNode { Value: { } name } && entry.Value is YamlScalarNode { Value: { } value })
                {
                    result[name] = value;
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<string> StringSequence(YamlMappingNode parent, string key)
    {
        if (!Child(parent, key, out var node) || node is not YamlSequenceNode sequence)
        {
            return [];
        }

        return [.. sequence.Children.OfType<YamlScalarNode>().Select(item => item.Value ?? string.Empty)];
    }

    private static string RawJson(YamlMappingNode parent, string key)
    {
        _ = Child(parent, key, out var node);

        return node switch
        {
            YamlScalarNode { Value: { } scalar } => scalar,
            null => string.Empty,
            _ => YamlJson.Serialize(node),
        };
    }

    private static int? Integer(YamlMappingNode parent, string key) =>
        Child(parent, key, out var node) && node is YamlScalarNode { Value: { } scalar } && int.TryParse(scalar, out var value)
            ? value
            : null;

    private static string Scalar(YamlMappingNode parent, string key) =>
        Child(parent, key, out var node) && node is YamlScalarNode { Value: { } scalar } ? scalar : string.Empty;

    private static bool Child(YamlMappingNode parent, string key, out YamlNode? value) =>
        parent.Children.TryGetValue(new YamlScalarNode(key), out value);

    private static YamlMappingNode LoadRoot(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var stream = new YamlStream();

        using (var reader = new StreamReader(path))
        {
            stream.Load(reader);
        }

        return stream.Documents is [{ RootNode: YamlMappingNode root }, ..] ? root : [];
    }

    private void Write(YamlMappingNode root)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Serialize(root));
        File.Move(temporary, path, overwrite: true);
    }
}
