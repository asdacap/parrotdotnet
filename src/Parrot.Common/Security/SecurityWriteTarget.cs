namespace Parrot.Security;

internal sealed record SecurityWriteTarget
{
    private SecurityWriteTarget(string path, SecurityWriteTargetKind kind)
    {
        Path = path;
        Kind = kind;
    }

    public string Path { get; }

    public SecurityWriteTargetKind Kind { get; }

    public static SecurityWriteTarget Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The security write target path must be absolute.", nameof(path));
        }

        var canonical = ResolvePhysicalPath(path);
        var kind = Directory.Exists(canonical)
            ? SecurityWriteTargetKind.Directory
            : File.Exists(canonical)
                ? SecurityWriteTargetKind.File
                : throw new FileNotFoundException("The security write target does not exist.", canonical);
        return new SecurityWriteTarget(canonical, kind);
    }

    public void Validate()
    {
        SecurityWriteTarget current;

        try
        {
            current = Resolve(Path);
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The security write target changed after approval.", failure);
        }

        if (current.Kind != Kind || !string.Equals(current.Path, Path, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The security write target changed after approval.");
        }
    }

    internal bool Contains(SecurityWriteTarget other) => Includes(other.Path);

    internal bool Includes(string path)
    {
        if (Kind != SecurityWriteTargetKind.Directory)
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
            ?? throw new ArgumentException("The security write target must have a root.", nameof(path));
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
