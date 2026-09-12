using System.Text;

namespace Parrot.Process;

internal sealed class SeatbeltPolicy
{
    private readonly List<AccessRule> _rules = [];

    public SeatbeltPolicy() =>
        _rules.Add(new AccessRule(Path.DirectorySeparatorChar.ToString(), true, false));

    public void AllowRead(string path)
    {
        var access = Evaluate(path);
        Set(path, read: true, access.Write);
    }

    public void AllowWrite(string path) => Set(path, read: true, write: true);

    public void DenyRead(string path) => Set(path, read: false, write: false);

    public void DenyWrite(string path)
    {
        var access = Evaluate(path);
        Set(path, access.Read, write: false);
    }

    public SeatbeltPolicySnapshot Capture()
    {
        var paths = _rules.Select(rule => rule.Path)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path.Length)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var states = paths.ToDictionary(path => path, Evaluate, StringComparer.Ordinal);
        var readTransitions = paths.Where(path => IsTransition(path, paths, states, access => access.Read)).ToArray();
        var writeTransitions = paths.Where(path => IsTransition(path, paths, states, access => access.Write)).ToArray();
        var text = new StringBuilder("(version 1)\n(allow default)\n");

        AppendDenyRules(text, "file-read*", readTransitions, states, access => access.Read);
        AppendDenyRules(text, "file-write*", writeTransitions, states, access => access.Write);
        return new SeatbeltPolicySnapshot(text.ToString());
    }

    private static void AppendDenyRules(
        StringBuilder text,
        string operation,
        string[] transitions,
        IReadOnlyDictionary<string, Access> states,
        Func<Access, bool> select)
    {
        foreach (var denied in transitions.Where(path => !select(states[path])))
        {
            var exceptions = transitions
                .Where(path => select(states[path]) && Contains(denied, path))
                .Where(path => !transitions.Any(parent =>
                    !string.Equals(parent, denied, StringComparison.Ordinal)
                    && !string.Equals(parent, path, StringComparison.Ordinal)
                    && Contains(denied, parent)
                    && Contains(parent, path)))
                .ToArray();
            _ = text.Append("(deny ").Append(operation);
            AppendDeniedFilter(text, denied, exceptions);
            _ = text.AppendLine(")");
        }
    }

    private static bool IsTransition(
        string path,
        IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, Access> states,
        Func<Access, bool> select)
    {
        var parent = paths
            .Where(candidate => !string.Equals(candidate, path, StringComparison.Ordinal) && Contains(candidate, path))
            .MaxBy(candidate => candidate.Length);
        return parent is null || select(states[parent]) != select(states[path]);
    }

    private static void AppendDeniedFilter(StringBuilder text, string denied, string[] exceptions)
    {
        var root = Path.GetPathRoot(denied) ?? Path.DirectorySeparatorChar.ToString();
        var deniesEverything = string.Equals(
            Path.TrimEndingDirectorySeparator(denied),
            Path.TrimEndingDirectorySeparator(root),
            StringComparison.Ordinal);

        if (exceptions.Length == 0)
        {
            if (!deniesEverything)
            {
                _ = text.Append(' ');
                AppendPathFilter(text, denied);
            }

            return;
        }

        if (deniesEverything)
        {
            _ = text.Append(" (require-not ");
            AppendAnyFilter(text, exceptions);
            _ = text.Append(')');
            return;
        }

        _ = text.Append(" (require-all ");
        AppendPathFilter(text, denied);
        _ = text.Append(" (require-not ");
        AppendAnyFilter(text, exceptions);
        _ = text.Append("))");
    }

    private static void AppendAnyFilter(StringBuilder text, string[] paths)
    {
        if (paths.Length == 1)
        {
            AppendPathFilter(text, paths[0]);
            return;
        }

        _ = text.Append("(require-any");
        foreach (var path in paths)
        {
            _ = text.Append(' ');
            AppendPathFilter(text, path);
        }

        _ = text.Append(')');
    }

    private static void AppendPathFilter(StringBuilder text, string path)
    {
        var filter = File.Exists(path) ? "literal" : "subpath";
        _ = text.Append('(').Append(filter).Append(" \"").Append(Escape(path)).Append("\")");
    }

    private static string Escape(string value)
    {
        if (value.Any(character => character is < ' ' or '\u007f'))
        {
            throw new InvalidOperationException("Seatbelt paths cannot contain control characters.");
        }

        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private Access Evaluate(string path)
    {
        var canonical = Normalize(path);
        return _rules.Last(rule => Contains(rule.Path, canonical)).Access;
    }

    private void Set(string path, bool read, bool write)
    {
        if (write && !read)
        {
            throw new InvalidOperationException("Seatbelt cannot grant write access without read access.");
        }

        _rules.Add(new AccessRule(Normalize(path), read, write));
    }

    private readonly record struct Access(bool Read, bool Write);

    private sealed record AccessRule(string Path, bool Read, bool Write)
    {
        public Access Access { get; } = new(Read, Write);
    }
}
