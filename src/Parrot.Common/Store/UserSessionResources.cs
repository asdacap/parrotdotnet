using Parrot.State;

namespace Parrot.Store;

internal sealed class UserSessionResources
{
    public UserSessionResources(StatePaths paths, UserSessionId id, ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(workspace);

        Id = id;
        Workspace = workspace;
        SessionsDirectory = PlatformPath.Normalize(Path.Combine(paths.State, "sessions"));
        Root = RequireContained(SessionsDirectory, id.Value);
        SocketPath = RequireContained(Root, "parrot.sock");
        MetadataPath = RequireContained(Root, "meta.json");
        DatabasePath = RequireContained(Root, "session.db");
        LogPath = RequireContained(Root, "session.log");
        ArtifactDirectory = RequireContained(Root, "artifacts");
        QueueDirectory = RequireContained(Root, "queues");
        AgentQueueRootDirectory = RequireContained(QueueDirectory, "agents");
        ScratchDirectory = RequireContained(Root, "scratch");
        AgentsDirectory = RequireContained(Root, "root-agents");
    }

    public UserSessionId Id { get; }

    public ProjectWorkspace Workspace { get; }

    public string SessionsDirectory { get; }

    public string Root { get; }

    public Lock MetadataGate { get; } = new();

    public string SocketPath { get; }

    public string MetadataPath { get; }

    public string DatabasePath { get; }

    public string LogPath { get; }

    public string ArtifactDirectory { get; }

    public string QueueDirectory { get; }

    public string AgentQueueRootDirectory { get; }

    public string ScratchDirectory { get; }

    public string AgentsDirectory { get; }

    public bool Owns(string path) => PlatformPath.Contains(Root, Path.GetFullPath(path));

    public string AgentQueueDirectory(string sessionId) =>
        AgentPath(AgentQueueRootDirectory, sessionId);

    public AgentScratchDirectory AgentScratch(IReadOnlyList<string> namePath)
    {
        ArgumentNullException.ThrowIfNull(namePath);
        if (namePath.Count == 0)
        {
            throw new ArgumentException("An agent name path must have at least the root agent name.", nameof(namePath));
        }

        var path = AgentPath(AgentsDirectory, namePath[0]);
        foreach (var name in namePath.Skip(1))
        {
            if (AgentScratchDirectory.ReservedNames.Contains(name))
            {
                throw new ArgumentException($"An agent name is reserved: {name}", nameof(namePath));
            }

            path = AgentPath(path, name);
        }

        return new(path);
    }

    private static string AgentPath(string root, string segment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment);
        if (segment is "." or ".."
            || segment.Any(char.IsWhiteSpace)
            || segment.Contains('/', StringComparison.Ordinal)
            || segment.Contains('\\', StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(segment), segment, StringComparison.Ordinal))
        {
            throw new ArgumentException("An agent path segment must be one path segment.", nameof(segment));
        }

        return RequireContained(root, segment);
    }

    private static string RequireContained(string root, string name)
    {
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!PlatformPath.Contains(root, full))
        {
            throw new InvalidOperationException("A user session resource escaped its private root.");
        }

        return full;
    }
}
