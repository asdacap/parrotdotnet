using System.Text.Json;
using Parrot.State;

namespace Parrot.Store;

internal sealed class SessionCatalog
{
    private readonly string _sessionsDirectory;

    public SessionCatalog(StatePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _sessionsDirectory = Path.GetFullPath(Path.Combine(paths.State, "sessions"));
    }

    public SessionCatalogEntry? Find(UserSessionId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var directory = Path.Combine(_sessionsDirectory, id.Value);
        return Directory.Exists(directory) ? Read(directory, id) : null;
    }

    public IReadOnlyList<SessionCatalogEntry> List()
    {
        if (!Directory.Exists(_sessionsDirectory))
        {
            return [];
        }

        var listed = new List<SessionCatalogEntry>();
        IEnumerable<string> directories;

        try
        {
            directories = [.. Directory.EnumerateDirectories(_sessionsDirectory)];
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var directory in directories)
        {
            if (!UserSessionId.TryParse(Path.GetFileName(directory), out var id))
            {
                continue;
            }

            listed.Add(Read(directory, id));
        }

        return listed;
    }

    private static SessionCatalogEntry Read(string directory, UserSessionId expectedId)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                return Corrupt(expectedId);
            }

            var metadataPath = Path.Combine(directory, "meta.json");
            if (!File.Exists(metadataPath)
                || (File.GetAttributes(metadataPath) & FileAttributes.ReparsePoint) != 0)
            {
                return Corrupt(expectedId);
            }

            var metadata = JsonSerializer.Deserialize(
                File.ReadAllText(metadataPath), StoreJsonContext.Default.SessionMeta);
            if (metadata is null || !UserSessionId.TryParse(metadata.Id, out var metadataId)
                || !metadataId.Equals(expectedId) || string.IsNullOrEmpty(metadata.WorkingDirectory)
                || !Path.IsPathFullyQualified(metadata.WorkingDirectory) || string.IsNullOrEmpty(metadata.ProviderId)
                || string.IsNullOrEmpty(metadata.Model))
            {
                return Corrupt(expectedId);
            }

            return new SessionCatalogEntry
            {
                Id = metadataId,
                State = SessionCatalogState.Inactive,
                WorkingDirectory = metadata.WorkingDirectory,
                RootAgentName = metadata.RootAgentName,
                ProviderId = metadata.ProviderId,
                Model = metadata.Model,
                Selector = metadata.Selector,
                Mode = metadata.Mode,
                CreatedAt = metadata.CreatedAt,
            };
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException)
        {
            return Corrupt(expectedId);
        }
    }

    private static SessionCatalogEntry Corrupt(UserSessionId id) =>
        new() { Id = id, State = SessionCatalogState.Corrupt };
}
