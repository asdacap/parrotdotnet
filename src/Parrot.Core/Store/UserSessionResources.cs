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
        Root = RequireContained(SessionsDirectory, Path.Combine(SessionsDirectory, id.Value));
        MetadataPath = RequireContained(Root, Path.Combine(Root, "meta.json"));
        DatabasePath = RequireContained(Root, Path.Combine(Root, "session.db"));
        ArtifactDirectory = RequireContained(Root, Path.Combine(Root, "artifacts"));
        QueueDirectory = RequireContained(Root, Path.Combine(Root, "queues"));
        AgentQueueRootDirectory = RequireContained(QueueDirectory, Path.Combine(QueueDirectory, "agents"));
        ScratchRootDirectory = RequireContained(Root, Path.Combine(Root, "scratch"));
    }

    public UserSessionId Id { get; }

    public ProjectWorkspace Workspace { get; }

    public string SessionsDirectory { get; }

    public string Root { get; }

    public string MetadataPath { get; }

    public string DatabasePath { get; }

    public string ArtifactDirectory { get; }

    public string QueueDirectory { get; }

    public string AgentQueueRootDirectory { get; }

    public string ScratchRootDirectory { get; }

    public bool Owns(string path) => Contains(Root, Path.GetFullPath(path));

    public string AgentQueueDirectory(string sessionId) =>
        AgentPath(AgentQueueRootDirectory, sessionId);

    public AgentScratchDirectory AgentScratch(string sessionId) =>
        new(AgentPath(ScratchRootDirectory, sessionId));

    public string AgentHistoryFile(string sessionId) => AgentScratch(sessionId).HistoryPath;

    private static string AgentPath(string root, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId is "." or ".."
            || sessionId.Any(char.IsWhiteSpace)
            || sessionId.Contains('/', StringComparison.Ordinal)
            || sessionId.Contains('\\', StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(sessionId), sessionId, StringComparison.Ordinal))
        {
            throw new ArgumentException("An agent session id must be one path segment.", nameof(sessionId));
        }

        return RequireContained(root, Path.Combine(root, sessionId));
    }

    private static string RequireContained(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (!Contains(root, full))
        {
            throw new InvalidOperationException("A user session resource escaped its private root.");
        }

        return full;
    }

    private static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
