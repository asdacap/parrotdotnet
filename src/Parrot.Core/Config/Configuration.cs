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

    public static Configuration Load(string path) =>
        new(path) { Model = ReadScalar(path, ModelKey) };

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

    private static string ReadScalar(string path, string key)
    {
        return LoadRoot(path).Children.TryGetValue(new YamlScalarNode(key), out var value)
            && value is YamlScalarNode { Value: { } scalar }
            ? scalar
            : string.Empty;
    }

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
