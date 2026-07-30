namespace Parrot.Permissions;

internal sealed record SandboxWriteTarget
{
    private SandboxWriteTarget(string path, SandboxWriteTargetKind kind)
    {
        Path = path;
        Kind = kind;
    }

    public string Path { get; }

    public SandboxWriteTargetKind Kind { get; }

    public static SandboxWriteTarget Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The sandbox write target path must be absolute.", nameof(path));
        }

        var canonical = ResolvePhysicalPath(path);
        var kind = Directory.Exists(canonical)
            ? SandboxWriteTargetKind.Directory
            : File.Exists(canonical)
                ? SandboxWriteTargetKind.File
                : throw new FileNotFoundException("The sandbox write target does not exist.", canonical);
        return new SandboxWriteTarget(canonical, kind);
    }

    public void Validate()
    {
        SandboxWriteTarget current;

        try
        {
            current = Resolve(Path);
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The sandbox write target changed after approval.", failure);
        }

        if (current.Kind != Kind || !string.Equals(current.Path, Path, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The sandbox write target changed after approval.");
        }
    }

    internal bool Contains(SandboxWriteTarget other) => Includes(other.Path);

    internal bool Includes(string path)
    {
        if (Kind != SandboxWriteTargetKind.Directory)
        {
            return string.Equals(Path, path, StringComparison.Ordinal);
        }

        var relative = System.IO.Path.GetRelativePath(Path, path);
        return relative == "." ||
            (!System.IO.Path.IsPathRooted(relative) && relative != ".." &&
             !relative.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !relative.StartsWith($"..{System.IO.Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string ResolvePhysicalPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(full)
            ?? throw new ArgumentException("The sandbox write target must have a root.", nameof(path));
        var relative = System.IO.Path.GetRelativePath(root, full);
        var current = root;

        foreach (var part in relative.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            current = target?.FullName ?? current;
        }

        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(current));
    }
}
