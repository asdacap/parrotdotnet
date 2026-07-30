using System.Text.Json;
using System.Text.RegularExpressions;
using Parrot.Security;

namespace Parrot.Tools;

internal sealed partial class GlobTool(ToolWorkspace workspace, SecurityProfile securityProfile) : ITool
{
    private const int MaxResults = 1000;
    private const int MaxVisited = 100_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public string Name => "glob";

    public string Description =>
        "Find workspace paths with deterministic glob matching, including **.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"pattern":{"type":"string"}},"required":["pattern"],"additionalProperties":false}
        """;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        string pattern;

        try
        {
            using var arguments = new ToolArguments(argumentsJson);
            pattern = arguments.RequiredString("pattern");
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return $"error: {failure.Message}";
        }

        if (pattern.Length == 0 || pattern.Contains('\0', StringComparison.Ordinal))
        {
            return "error: glob pattern must be a non-empty string without NUL";
        }

        if (Path.IsPathFullyQualified(pattern) || pattern.Contains("..", StringComparison.Ordinal))
        {
            return "error: glob pattern must be a relative workspace path without traversal";
        }

        Regex regex;

        try
        {
            regex = new Regex(
                GlobToRegex(pattern),
                RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                Timeout);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException)
        {
            return $"error: invalid glob pattern: {failure.Message}";
        }

        (string Lexical, string Physical) root;
        try
        {
            root = workspace.ResolveRead(".");
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException)
        {
            return $"error: {failure.Message}";
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(Timeout);

        try
        {
            var results = new List<string>();
            var visited = 0;
            Walk(
                workspace,
                root.Physical,
                root.Physical,
                regex,
                results,
                ref visited,
                securityProfile,
                timeoutCancellation.Token);

            if (results.Count == 0)
            {
                return string.Empty;
            }

            results.Sort(StringComparer.Ordinal);
            var truncated = results.Count > MaxResults;

            if (truncated)
            {
                results.RemoveRange(MaxResults, results.Count - MaxResults);
            }

            var output = string.Join('\n', results) + "\n";
            return truncated ? output + "[glob results truncated]\n" : output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "error: glob timed out";
        }
    }

    private static void Walk(
        ToolWorkspace workspace,
        string root,
        string current,
        Regex regex,
        List<string> results,
        ref int visited,
        SecurityProfile securityProfile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!securityProfile.AllowsRead(current))
        {
            return;
        }

        string[] entries;

        try
        {
            entries = [.. Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal)];
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            visited++;

            if (visited > MaxVisited || results.Count >= MaxResults)
            {
                return;
            }

            var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
            (string Lexical, string Physical) resolved;

            try
            {
                resolved = workspace.ResolveRead(relative);
            }
            catch (Exception failure) when (failure is InvalidOperationException or IOException)
            {
                continue;
            }

            if (!securityProfile.AllowsRead(resolved.Lexical) || !securityProfile.AllowsRead(resolved.Physical))
            {
                continue;
            }

            var attributes = File.GetAttributes(entry);
            var isSymlink = (attributes & FileAttributes.ReparsePoint) != 0;
            var isRealDirectory = (attributes & FileAttributes.Directory) != 0 && !isSymlink;

            if (regex.IsMatch(relative))
            {
                results.Add(isRealDirectory || (isSymlink && Directory.Exists(entry)) ? relative + "/" : relative);
            }

            if (isRealDirectory)
            {
                Walk(workspace, root, entry, regex, results, ref visited, securityProfile, cancellationToken);
            }
        }
    }

    private static string GlobToRegex(string pattern)
    {
        var regex = new System.Text.StringBuilder("^");
        var index = 0;

        while (index < pattern.Length)
        {
            if (pattern[index] == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    index += 2;

                    if (index < pattern.Length && pattern[index] == '/')
                    {
                        _ = regex.Append("(.*/)?");
                        index++;
                    }
                    else
                    {
                        _ = regex.Append(".*");
                    }
                }
                else
                {
                    _ = regex.Append("[^/]*");
                    index++;
                }
            }
            else if (pattern[index] == '?')
            {
                _ = regex.Append("[^/]");
                index++;
            }
            else
            {
                _ = regex.Append(Regex.Escape(pattern[index].ToString()));
                index++;
            }
        }

        _ = regex.Append('$');
        return regex.ToString();
    }
}
