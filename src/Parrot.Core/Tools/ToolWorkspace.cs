using Parrot.Permissions;
using Parrot.Security;
using Parrot.State;

namespace Parrot.Tools;

internal sealed class ToolWorkspace(string workingDirectory, ToolFileSystemPolicy fileSystemPolicy)
{
    private readonly ToolFileSystemPolicy _fileSystemPolicy = fileSystemPolicy;

    public ToolWorkspace(string workingDirectory)
        : this(workingDirectory, new ToolFileSystemPolicy(StatePaths.ResolveFromEnvironment()))
    {
    }

    public string Root { get; } = Canonicalize(workingDirectory);

    public (string Lexical, string Physical) ResolveRead(string path)
    {
        var lexical = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Root, path));

        RequireContained(lexical);
        _fileSystemPolicy.RequireUnprotected(lexical);
        var physical = ResolveLinks(lexical);
        _fileSystemPolicy.RequireUnprotected(physical);
        return (lexical, physical);
    }

    public ToolMutationPath ResolveMutation(
        string path,
        bool create,
        SecurityProfile security,
        SandboxWriteGrantSnapshot writeGrants)
    {
        var lexical = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Root, path));
        var inWorkspace = Contained(lexical);

        _fileSystemPolicy.RequireUnprotected(lexical);

        var physical = ResolveMutationPath(lexical, path, create);
        _fileSystemPolicy.RequireUnprotected(physical);
        writeGrants.Validate(lexical);
        if (!string.Equals(physical, lexical, StringComparison.Ordinal))
        {
            writeGrants.Validate(physical);
        }

        if (!AllowsMutation(lexical, security, writeGrants, inWorkspace)
            || !AllowsMutation(physical, security, writeGrants, inWorkspace))
        {
            throw new InvalidOperationException($"Write access denied for '{path}'.");
        }

        if (create)
        {
            RequireWritableMissingParents(physical, path, security, writeGrants, inWorkspace);
        }

        return new ToolMutationPath(physical, DisplayPath(physical));
    }

    private static string ResolveMutationPath(string full, string requested, bool create)
    {
        var root = Path.GetPathRoot(full) ?? throw new InvalidOperationException($"Invalid path '{requested}'.");
        var relative = Path.GetRelativePath(root, full);
        var current = Path.TrimEndingDirectorySeparator(root);
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            var kind = FileMutation.Inspect(current);
            if (kind == FileMutationEntryKind.Missing)
            {
                if (create)
                {
                    break;
                }

                throw new FileNotFoundException($"Source '{requested}' is missing.");
            }

            if (kind == FileMutationEntryKind.SymbolicLink)
            {
                throw new InvalidOperationException($"Path '{requested}' traverses a symbolic link.");
            }

            if (index < parts.Length - 1 && kind != FileMutationEntryKind.Directory)
            {
                throw new InvalidOperationException($"Parent of '{requested}' is not a directory.");
            }
        }

        return full;
    }

    private static void RequireWritableMissingParents(
        string path,
        string requested,
        SecurityProfile security,
        SandboxWriteGrantSnapshot writeGrants,
        bool inWorkspace)
    {
        for (var parent = Path.GetDirectoryName(path); parent is not null && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent))
        {
            if (!AllowsMutation(parent, security, writeGrants, inWorkspace))
            {
                throw new InvalidOperationException($"Write access denied for '{requested}'.");
            }
        }
    }

    private static bool AllowsMutation(
        string path,
        SecurityProfile security,
        SandboxWriteGrantSnapshot writeGrants,
        bool inWorkspace)
    {
        var staticallyAllowed = security.AllowsWrite(path)
            && (inWorkspace || HasExternalCapability(path, security));
        var granted = !security.ReadOnly
            && !IsExplicitlyDenied(path, security)
            && AllowsGrant(path, writeGrants);
        return staticallyAllowed || granted;
    }

    private static bool AllowsGrant(string path, SandboxWriteGrantSnapshot writeGrants) =>
        writeGrants.Targets.Any(target => target.Kind == SandboxWriteTargetKind.Directory
            ? PathContains(target.Path, path)
            : string.Equals(target.Path, path, StringComparison.Ordinal));

    private static bool IsExplicitlyDenied(string path, SecurityProfile security)
    {
        var denied = false;
        foreach (var rule in security.WithoutRuntimeCapabilities().Rules)
        {
            if (!PathContains(rule.Path, path))
            {
                continue;
            }

            if (rule.Action is SandboxRuleAction.DenyRead or SandboxRuleAction.DenyWrite)
            {
                denied = true;
            }
            else if (rule.Action == SandboxRuleAction.AllowWrite)
            {
                denied = false;
            }
        }

        return denied;
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
