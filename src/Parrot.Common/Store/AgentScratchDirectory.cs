using System.Collections.Immutable;

namespace Parrot.Store;

internal sealed class AgentScratchDirectory
{
    public static readonly ImmutableHashSet<string> ReservedNames =
        ["scratch", "history.jsonl", "blobs", "plan", "last_request.json"];

    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public AgentScratchDirectory(string root)
    {
        Root = PlatformPath.Normalize(root);
        ScratchPath = Contain("scratch");
        HistoryPath = Contain("history.jsonl");
        BlobDirectory = Contain("blobs");
        PlanDirectory = Contain("plan");
        Provision();
    }

    public string Root { get; }

    public string ScratchPath { get; }

    public string HistoryPath { get; }

    public string BlobDirectory { get; }

    public string PlanDirectory { get; }

    public bool Contains(string path)
    {
        var relative = Path.GetRelativePath(Root, PlatformPath.Normalize(path));
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    public void Provision()
    {
        ProvisionDirectory(Root);
        ProvisionDirectory(ScratchPath);
        ProvisionDirectory(BlobDirectory);
    }

    public void ProvisionPlanDirectory() => ProvisionDirectory(PlanDirectory);

    private string Contain(string name)
    {
        var path = PlatformPath.Normalize(Path.Combine(Root, name));
        if (!Contains(path))
        {
            throw new InvalidOperationException("An agent scratch resource escaped its root.");
        }

        return path;
    }

    private void ProvisionDirectory(string path)
    {
        if (!Contains(path))
        {
            throw new InvalidOperationException("An agent scratch resource escaped its root.");
        }

        if (Path.Exists(path) && !Directory.Exists(path))
        {
            throw new InvalidOperationException($"Agent scratch path is not a directory: {path}");
        }

        var directory = Directory.CreateDirectory(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"Agent scratch directory cannot be a symbolic link: {path}");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, DirectoryMode);
        }
    }
}
