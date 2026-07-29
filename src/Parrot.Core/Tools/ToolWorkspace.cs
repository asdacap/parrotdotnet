using Parrot.Security;

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

    public PatchMutationPath ResolvePatchMutation(string path, bool create, SecurityProfile security)
    {
        var lexical = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Root, path));
        var inWorkspace = Contained(lexical);

        if (!inWorkspace && !HasExternalCapability(lexical, security))
        {
            throw new InvalidOperationException($"Write access denied for '{path}'.");
        }

        var physical = ResolvePatchPath(lexical, path, create);
        if (!security.AllowsWrite(physical) || (!inWorkspace && !HasExternalCapability(physical, security)))
        {
            throw new InvalidOperationException($"Write access denied for '{path}'.");
        }

        if (create)
        {
            RequireWritableMissingParents(physical, path, security, inWorkspace);
        }

        return new PatchMutationPath(physical, DisplayPath(physical));
    }

    private static string ResolvePatchPath(string full, string requested, bool create)
    {
        var root = Path.GetPathRoot(full) ?? throw new InvalidOperationException($"Invalid path '{requested}'.");
        var relative = Path.GetRelativePath(root, full);
        var current = Path.TrimEndingDirectorySeparator(root);
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            if (!Path.Exists(current))
            {
                if (create)
                {
                    break;
                }

                throw new FileNotFoundException($"Source '{requested}' is missing.");
            }

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Path '{requested}' traverses a symbolic link.");
            }

            if (index < parts.Length - 1 && (attributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidOperationException($"Parent of '{requested}' is not a directory.");
            }
        }

        return full;
    }

    private static void RequireWritableMissingParents(string path, string requested, SecurityProfile security, bool inWorkspace)
    {
        for (var parent = Path.GetDirectoryName(path); parent is not null && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent))
        {
            if (!security.AllowsWrite(parent) || (!inWorkspace && !HasExternalCapability(parent, security)))
            {
                throw new InvalidOperationException($"Write access denied for '{requested}'.");
            }
        }
    }

    private static bool HasExternalCapability(string path, SecurityProfile security)
    {
        var allowed = false;
        foreach (var rule in security.Rules)
        {
            if (!PathContains(rule.Path, path))
            {
                continue;
            }

            if (rule.Action == SandboxRuleAction.DenyWrite)
            {
                allowed = false;
            }
            else if (rule.Action == SandboxRuleAction.AllowWrite
                && (string.Equals(rule.Path, path, StringComparison.Ordinal) || Directory.Exists(rule.Path)))
            {
                allowed = true;
            }
        }

        return allowed;
    }

    private static bool PathContains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string Canonicalize(string path)
    {
        var info = new DirectoryInfo(Path.GetFullPath(path));
        var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
        return Path.TrimEndingDirectorySeparator((resolved ?? info).FullName);
    }

    private string DisplayPath(string physical) => Path.GetRelativePath(Root, physical);

    private void RequireContained(string full)
    {
        if (!Contained(full))
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

            FileSystemInfo link = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
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
