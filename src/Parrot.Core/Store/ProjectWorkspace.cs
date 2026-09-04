namespace Parrot.Store;

internal sealed class ProjectWorkspace : IEquatable<ProjectWorkspace>
{
    private ProjectWorkspace(string launchDirectory, string physicalIdentity, GitRepository repository)
    {
        LaunchDirectory = launchDirectory;
        PhysicalIdentity = physicalIdentity;
        IsGitRepository = repository.IsRepository;
        WritableRoots = ResolveWritableRoots(launchDirectory, physicalIdentity, repository.WritableRoot);
    }

    public string LaunchDirectory { get; }

    public string PhysicalIdentity { get; }

    public bool IsGitRepository { get; }

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

        var physicalIdentity = ResolvePhysicalIdentity(launchDirectory);
        return new ProjectWorkspace(launchDirectory, physicalIdentity, FindGitRepository(launchDirectory));
    }

    public bool Equals(ProjectWorkspace? other) =>
        other is not null && string.Equals(PhysicalIdentity, other.PhysicalIdentity, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ProjectWorkspace other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(PhysicalIdentity);

    private static IReadOnlyList<string> ResolveWritableRoots(string launchDirectory, string physicalIdentity, string? repositoryRoot)
    {
        var roots = new List<string>();
        if (repositoryRoot is not null)
        {
            roots.Add(repositoryRoot);
        }

        roots.Add(launchDirectory);
        roots.Add(physicalIdentity);
        return [.. roots.Distinct(StringComparer.Ordinal)];
    }

    private static GitRepository FindGitRepository(string workingDirectory)
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
                    return new GitRepository(true, directory.FullName);
                }

                if (File.Exists(gitPath))
                {
                    var gitDirectory = ReadGitDirectory(gitPath);
                    return gitDirectory is not null && Directory.Exists(gitDirectory)
                        ? new GitRepository(true, FindLinkedRepositoryRoot(gitPath))
                        : new GitRepository(false, null);
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }

        return new GitRepository(false, null);
    }

    private static string? FindLinkedRepositoryRoot(string gitPath)
    {
        var worktreeRoot = Path.GetDirectoryName(gitPath);
        var gitDirectory = ReadGitDirectory(gitPath);
        if (worktreeRoot is null || gitDirectory is null)
        {
            return null;
        }

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

    private static string? ReadGitDirectory(string gitPath)
    {
        var gitFile = File.ReadAllText(gitPath).Trim();
        var worktreeRoot = Path.GetDirectoryName(gitPath);
        return worktreeRoot is not null && gitFile.StartsWith("gitdir: ", StringComparison.Ordinal)
            && gitFile.Length > 8
            ? ResolvePath(worktreeRoot, gitFile[8..])
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

    private sealed record GitRepository(bool IsRepository, string? WritableRoot);
}
