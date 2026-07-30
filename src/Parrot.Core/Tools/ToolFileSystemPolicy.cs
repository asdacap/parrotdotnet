using Parrot.State;

namespace Parrot.Tools;

internal sealed class ToolFileSystemPolicy(StatePaths paths)
{
    private readonly HashSet<string> _protectedRoots = new(
        [
            CanonicalizeLexical(paths.State),
            CanonicalizePhysical(paths.State),
            CanonicalizeLexical(paths.Config),
            CanonicalizePhysical(paths.Config),
            CanonicalizeLexical(paths.Data),
            CanonicalizePhysical(paths.Data),
        ],
        PathComparer());

    public void RequireUnprotected(string path)
    {
        var full = Path.GetFullPath(path);
        if (_protectedRoots.Any(root => Contains(root, full)))
        {
            throw new InvalidOperationException("Access to protected application data is denied.");
        }
    }

    private static string CanonicalizeLexical(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string CanonicalizePhysical(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)
            ?? throw new InvalidOperationException($"Invalid protected root '{path}'.");
        var parts = Path.GetRelativePath(root, full)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = Path.TrimEndingDirectorySeparator(root);

        for (var index = 0; index < parts.Length; index++)
        {
            var candidate = Path.Combine(current, parts[index]);
            if (!Path.Exists(candidate))
            {
                return Path.TrimEndingDirectorySeparator(Path.Combine(
                    current,
                    Path.Combine(parts[index..])));
            }

            var attributes = File.GetAttributes(candidate);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                current = candidate;
                continue;
            }

            FileSystemInfo link = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : new FileInfo(candidate);
            current = link.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? candidate;
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static StringComparer PathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
