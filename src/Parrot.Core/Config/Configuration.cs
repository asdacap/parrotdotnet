using YamlDotNet.RepresentationModel;

namespace Parrot.Config;

// config.yaml: the shared, hand-editable configuration. Model-only today; a
// nested providers: map arrives with the provider registry.
//
// A write edits the YAML representation model and rewrites the file atomically,
// so changing one field keeps the other keys and their order. Comments are not
// preserved -- the representation model drops them -- which is the one way this
// is less faithful than upstream's update.go. Acceptable while the file is a
// handful of scalars; revisit when it gains structure worth commenting.
internal sealed class Configuration(string path)
{
    private const string ModelKey = "model";

    // A read-modify-write. Rename gives atomicity, not serialisation across
    // hosts, so this is last-write-wins for a global preference -- which is
    // what upstream accepts for the model too.
    public string Model { get; private set; } = string.Empty;

    // The configured providers, keyed by id. Empty is the common case: the
    // preset providers need only a credential, not a config entry.
    public IReadOnlyDictionary<string, ProviderConfig> Providers { get; private set; } =
        new Dictionary<string, ProviderConfig>(StringComparer.Ordinal);

    public static Configuration Load(string path)
    {
        var root = LoadRoot(path);

        return new(path)
        {
            Model = Scalar(root, ModelKey),
            Providers = ReadProviders(root),
        };
    }

    // Only the interactive /model reaches here; --model is a per-invocation
    // override that does not persist.
    public void SetModel(string model)
    {
        var root = LoadRoot(path);
        root.Children[new YamlScalarNode(ModelKey)] = new YamlScalarNode(model);

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Serialize(root));
        File.Move(temporary, path, overwrite: true);

        Model = model;
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
}
