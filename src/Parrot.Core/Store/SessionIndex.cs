using System.Text.Json;

namespace Parrot.Store;

// meta.json beside each session database, published by rename.
//
// Listing reads these and never another host's database: a reader cannot
// observe a rename half-written, and opening a database written by a different
// machine is exactly what the storage layout exists to prevent.
internal sealed class SessionIndex(string stateDirectory)
{
    public string DirectoryFor(string userSessionId) =>
        Path.Combine(stateDirectory, "sessions", userSessionId);

    public string DatabaseFor(string userSessionId) =>
        Path.Combine(DirectoryFor(userSessionId), "session.db");

    public string BlobDirectoryFor(string userSessionId) =>
        Path.Combine(DirectoryFor(userSessionId), "blob");

    public void Publish(SessionMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var directory = DirectoryFor(meta.Id);
        _ = Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, "meta.json");
        var staged = target + ".staging";

        File.WriteAllText(staged, JsonSerializer.Serialize(meta, StoreJsonContext.Default.SessionMeta));
        File.Move(staged, target, overwrite: true);
    }

    public IReadOnlyList<SessionMeta> List()
    {
        var sessions = Path.Combine(stateDirectory, "sessions");

        if (!Directory.Exists(sessions))
        {
            return [];
        }

        var listed = new List<SessionMeta>();

        foreach (var directory in Directory.EnumerateDirectories(sessions))
        {
            var meta = Path.Combine(directory, "meta.json");

            if (!File.Exists(meta))
            {
                continue;
            }

            var parsed = JsonSerializer.Deserialize(
                File.ReadAllText(meta), StoreJsonContext.Default.SessionMeta);

            if (parsed is not null)
            {
                listed.Add(parsed);
            }
        }

        return listed;
    }
}
