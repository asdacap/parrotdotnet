namespace Parrot.Store;

internal sealed class ProjectWorkspace : IEquatable<ProjectWorkspace>
{
    private ProjectWorkspace(string launchDirectory, string physicalIdentity)
    {
        LaunchDirectory = launchDirectory;
        PhysicalIdentity = physicalIdentity;
        WritableRoots = ResolveWritableRoots(launchDirectory, physicalIdentity);
    }

    public string LaunchDirectory { get; }

    public string PhysicalIdentity { get; }

    public IReadOnlyList<string> WritableRoots { get; }

    public static ProjectWorkspace FromLaunchDirectory(string launchDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launchDirectory);

        if (!Path.IsPathFullyQualified(launchDirectory))
        {
            throw new ArgumentException("The project launch directory must be absolute.", nameof(launchDirectory));
        }

        if (!Directory.Exists(launchDirectory))
        {
            throw new DirectoryNotFoundException($"Project launch directory does not exist: {launchDirectory}");
        }

        return new ProjectWorkspace(launchDirectory, ResolvePhysicalIdentity(launchDirectory));
    }

    public bool Equals(ProjectWorkspace? other) =>
        other is not null && string.Equals(PhysicalIdentity, other.PhysicalIdentity, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ProjectWorkspace other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(PhysicalIdentity);

    private static IReadOnlyList<string> ResolveWritableRoots(string launchDirectory, string physicalIdentity)
    {
        var roots = new List<string>();
        var repositoryRoot = FindGitRepositoryRoot(launchDirectory);
        if (repositoryRoot is not null)
        {
            roots.Add(repositoryRoot);
        }

        roots.Add(launchDirectory);
        roots.Add(physicalIdentity);
        return [.. roots.Distinct(StringComparer.Ordinal)];
    }

    private static string? FindGitRepositoryRoot(string workingDirectory)
    {
        try
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(workingDirectory));
                 directory is not null;
                 directory = directory.Parent)
            {
                var gitPath = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(gitPath))
                {
                    return directory.FullName;
                }

                if (File.Exists(gitPath))
                {
                    return FindLinkedRepositoryRoot(gitPath);
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static string? FindLinkedRepositoryRoot(string gitPath)
    {
        var gitFile = File.ReadAllText(gitPath).Trim();
        if (!gitFile.StartsWith("gitdir: ", StringComparison.Ordinal))
        {
            return null;
        }

        var worktreeRoot = Path.GetDirectoryName(gitPath);
        if (worktreeRoot is null)
        {
            return null;
        }

        var gitDirectory = ResolvePath(worktreeRoot, gitFile[8..]);
        var commonDirectoryPath = Path.Combine(gitDirectory, "commondir");
        var backlinkPath = Path.Combine(gitDirectory, "gitdir");
        if (!File.Exists(commonDirectoryPath) || !File.Exists(backlinkPath))
        {
            return null;
        }

        var backlink = ResolvePath(gitDirectory, File.ReadAllText(backlinkPath).Trim());
        if (!string.Equals(backlink, Path.GetFullPath(gitPath), StringComparison.Ordinal))
        {
            return null;
        }

        var commonDirectory = Path.TrimEndingDirectorySeparator(
            ResolvePath(gitDirectory, File.ReadAllText(commonDirectoryPath).Trim()));
        var worktreesDirectory = Path.Combine(commonDirectory, "worktrees");
        return Directory.Exists(commonDirectory)
               && string.Equals(Path.GetFileName(commonDirectory), ".git", StringComparison.Ordinal)
               && string.Equals(Path.GetDirectoryName(gitDirectory), worktreesDirectory, StringComparison.Ordinal)
            ? Path.GetDirectoryName(commonDirectory)
            : null;
    }

    private static string ResolvePath(string baseDirectory, string path) =>
        Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(baseDirectory, path));

    private static string ResolvePhysicalIdentity(string launchDirectory)
    {
        var full = Path.GetFullPath(launchDirectory);
        var root = Path.GetPathRoot(full)
            ?? throw new ArgumentException("The project launch directory must have a root.", nameof(launchDirectory));
        var relative = Path.GetRelativePath(root, full);
        var current = root;

        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);

            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            current = (info.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new DirectoryNotFoundException($"Project workspace link target does not exist: {current}"))
                .FullName;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }
}
