using Parrot.Security;

namespace Parrot.Tools;

internal sealed class ToolWorkspace(string workingDirectory)
{
    public string Root { get; } = Canonicalize(workingDirectory);

    public static bool AllowsRead((string Lexical, string Physical) path, SecurityProfile security) =>
        security.AllowsRead(path.Lexical) && security.AllowsRead(path.Physical);

    public (string Lexical, string Physical) ResolveRead(string path)
    {
        var lexical = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Root, path));

        var physical = ResolveLinks(lexical);
        return (lexical, physical);
    }

    public ToolMutationPath ResolveMutation(string path, bool create, SecurityProfile security)
    {
        var lexical = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Root, path));
        var physical = ResolveMutationPath(lexical, path, create);

        if (!security.AllowsWrite(lexical) || !security.AllowsWrite(physical))
        {
            throw new InvalidOperationException($"Write access denied for '{path}'.");
        }

        if (create)
        {
            RequireWritableMissingParents(physical, path, security);
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

    private static void RequireWritableMissingParents(string path, string requested, SecurityProfile security)
    {
        for (var parent = Path.GetDirectoryName(path); parent is not null && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent))
        {
            if (!security.AllowsWrite(parent))
            {
                throw new InvalidOperationException($"Write access denied for '{requested}'.");
            }
        }
    }

    private static string Canonicalize(string path)
    {
        var info = new DirectoryInfo(Path.GetFullPath(path));
        var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
        return Path.TrimEndingDirectorySeparator((resolved ?? info).FullName);
    }

    private static string ResolveLinks(string full)
    {
        var root = Path.GetPathRoot(full) ?? throw new InvalidOperationException($"Invalid path '{full}'.");
        var relative = Path.GetRelativePath(root, full);
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = Path.TrimEndingDirectorySeparator(root);

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
            current = target.FullName;
        }

        return current;
    }

    private string DisplayPath(string physical) => Path.GetRelativePath(Root, physical);
}
