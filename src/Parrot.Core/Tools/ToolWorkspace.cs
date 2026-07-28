namespace Parrot.Tools;

internal sealed class ToolWorkspace(string workingDirectory)
{
    public string Root { get; } = Canonicalize(workingDirectory);

    public (string Lexical, string Physical) ResolveRead(string path)
    {
        var lexical = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Root, path));

        RequireContained(lexical);
        return (lexical, ResolveLinks(lexical));
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
        try
        {
            _ = ResolveLinks(full);
        }
        catch (FileNotFoundException) when (allowMissing)
        {
        }
    }

    private string ResolveLinks(string full)
    {
        var relative = Path.GetRelativePath(Root, full);
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = Root;

        foreach (var part in parts)
        {
            current = Path.Combine(current, part);

            if (!Path.Exists(current))
            {
                continue;
            }

            var attributes = File.GetAttributes(current);

            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            FileSystemInfo link = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            var target = link.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new FileNotFoundException($"Symbolic link target for '{current}' is missing.");

            if (!Contained(target.FullName))
            {
                throw new InvalidOperationException("Path traverses a symbolic link outside the workspace.");
            }

            current = target.FullName;
        }

        return current;
    }

    private bool Contained(string path)
    {
        var relative = Path.GetRelativePath(Root, path);
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathFullyQualified(relative);
    }
}
