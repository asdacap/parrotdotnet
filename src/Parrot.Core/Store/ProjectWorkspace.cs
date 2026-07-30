namespace Parrot.Store;

internal sealed class ProjectWorkspace : IEquatable<ProjectWorkspace>
{
    private ProjectWorkspace(string launchDirectory, string physicalIdentity)
    {
        LaunchDirectory = launchDirectory;
        PhysicalIdentity = physicalIdentity;
    }

    public string LaunchDirectory { get; }

    public string PhysicalIdentity { get; }

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
