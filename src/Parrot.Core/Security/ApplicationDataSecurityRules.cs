using Parrot.State;

namespace Parrot.Security;

internal sealed class ApplicationDataSecurityRules
{
    private readonly SandboxRule[] _rules;

    public ApplicationDataSecurityRules(StatePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        _rules = [.. new[] { paths.State, paths.Config, paths.Data }
            .SelectMany(path => new[] { CanonicalizeLexical(path), CanonicalizePhysical(path) })
            .Distinct(comparer)
            .Select(path => new SandboxRule(path, SandboxRuleAction.DenyRead))];
    }

    public IReadOnlyList<SandboxRule> Rules => [.. _rules];

    private static string CanonicalizeLexical(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string CanonicalizePhysical(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)
            ?? throw new InvalidOperationException($"Invalid application data root '{path}'.");
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
}
