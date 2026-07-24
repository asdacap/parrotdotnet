namespace Parrot.Tools;

internal sealed class ToolWorkspace(string workingDirectory)
{
    public string Root { get; } = Canonicalize(workingDirectory);

    public string ResolveRead(string path)
    {
        var full = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Root, path));

        RequireContained(full);
        RequireNoLinkEscape(full, allowMissing: false);
        return full;
    }

    public string ResolveMutation(string path)
    {
        if (Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException($"Absolute path '{path}' is not permitted for mutations.");
        }

        var full = Path.GetFullPath(Path.Combine(Root, path));
        RequireContained(full);
        RequireNoLinkEscape(full, allowMissing: true);
        return full;
    }

    private static string Canonicalize(string path)
    {
        var info = new DirectoryInfo(Path.GetFullPath(path));
        var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
        return Path.TrimEndingDirectorySeparator((resolved ?? info).FullName);
    }

    private void RequireContained(string full)
    {
        var relative = Path.GetRelativePath(Root, full);

        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathFullyQualified(relative))
        {
            throw new InvalidOperationException("Path escapes the workspace.");
        }
    }

    private void RequireNoLinkEscape(string full, bool allowMissing)
    {
        var relative = Path.GetRelativePath(Root, full);
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = Root;

        foreach (var part in parts)
        {
            current = Path.Combine(current, part);

            if (!Path.Exists(current))
            {
                if (allowMissing)
                {
                    break;
                }

                continue;
            }

            var attributes = File.GetAttributes(current);

            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            var target = new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true);

            if (target is not null && !Contained(target.FullName))
            {
                throw new InvalidOperationException("Path traverses a symbolic link outside the workspace.");
            }
        }
    }

    private bool Contained(string path)
    {
        var relative = Path.GetRelativePath(Root, path);
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathFullyQualified(relative);
    }
}
