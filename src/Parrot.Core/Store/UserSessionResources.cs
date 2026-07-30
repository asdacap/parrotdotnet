using System.Collections.ObjectModel;
using Parrot.State;

namespace Parrot.Store;

internal sealed class UserSessionResources
{
    private readonly ReadOnlyCollection<string> _protectedRoots;

    public UserSessionResources(StatePaths paths, UserSessionId id, ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(workspace);

        Id = id;
        Workspace = workspace;
        SessionsDirectory = Path.GetFullPath(Path.Combine(paths.State, "sessions"));
        Root = RequireContained(SessionsDirectory, Path.Combine(SessionsDirectory, id.Value));
        MetadataPath = RequireContained(Root, Path.Combine(Root, "meta.json"));
        DatabasePath = RequireContained(Root, Path.Combine(Root, "session.db"));
        BlobDirectory = RequireContained(Root, Path.Combine(Root, "blob"));
        QueueDirectory = RequireContained(Root, Path.Combine(Root, "queues"));
        PlanDirectory = RequireContained(Root, Path.Combine(Root, "plan"));
        RuntimeDirectory = RequireContained(Root, Path.Combine(Root, "runtime"));
        RuntimeHomeDirectory = RequireContained(RuntimeDirectory, Path.Combine(RuntimeDirectory, "home"));
        CacheDirectory = RequireContained(RuntimeDirectory, Path.Combine(RuntimeDirectory, "cache"));
        TemporaryDirectory = RequireContained(RuntimeDirectory, Path.Combine(RuntimeDirectory, "tmp"));
        _protectedRoots = Array.AsReadOnly(
        [
            Path.GetFullPath(paths.State),
            SessionsDirectory,
            Path.GetFullPath(Path.Combine(paths.State, "owners")),
            Path.GetFullPath(paths.Control),
            Path.GetFullPath(paths.Config),
            Path.GetFullPath(paths.Data),
            Root,
        ]);
    }

    public UserSessionId Id { get; }

    public ProjectWorkspace Workspace { get; }

    public string SessionsDirectory { get; }

    public string Root { get; }

    public string MetadataPath { get; }

    public string DatabasePath { get; }

    public string BlobDirectory { get; }

    public string QueueDirectory { get; }

    public string PlanDirectory { get; }

    public string RuntimeDirectory { get; }

    public string RuntimeHomeDirectory { get; }

    public string CacheDirectory { get; }

    public string TemporaryDirectory { get; }

    public IReadOnlyList<string> ProtectedRoots => _protectedRoots;

    public bool Owns(string path) => Contains(Root, Path.GetFullPath(path));

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
