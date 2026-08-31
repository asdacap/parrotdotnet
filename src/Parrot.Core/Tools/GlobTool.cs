using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Parrot.Agent;
using Parrot.Security;

namespace Parrot.Tools;

internal sealed class GlobTool(ToolWorkspace workspace) : ITool
{
    private const int MaxResults = 1000;
    private const int MaxVisited = 100_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private enum WalkStopReason
    {
        Completed,
        ResultLimit,
        VisitLimit,
    }

    public string Name => "glob";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        string pattern;
        string path;

        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, FileToolJsonContext.Default.GlobToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            pattern = input.Pattern ?? throw new FormatException("Tool arguments require a string 'pattern'.");
            path = input.Path ?? string.Empty;
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
            return "error: glob pattern must be a relative search-root path without traversal";
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
            root = workspace.ResolveRead(path.Length == 0 ? "." : path);
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException)
        {
            return $"error: {failure.Message}";
        }

        if (!ToolWorkspace.AllowsRead(root, selection.SecurityProfile))
        {
            return "error: access denied";
        }

        if (!Directory.Exists(root.Physical))
        {
            return "error: no such directory";
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(Timeout);

        try
        {
            var results = new List<string>();
            var visited = 0;
            var stopReason = Walk(
                workspace,
                root,
                string.Empty,
                regex,
                results,
                ref visited,
                selection.SecurityProfile,
                timeoutCancellation.Token);

            if (results.Count == 0 && stopReason == WalkStopReason.Completed)
            {
                return string.Empty;
            }

            results.Sort(StringComparer.Ordinal);
            if (results.Count > MaxResults)
            {
                results.RemoveRange(MaxResults, results.Count - MaxResults);
            }

            var output = results.Count == 0 ? string.Empty : string.Join('\n', results) + "\n";
            return stopReason switch
            {
                WalkStopReason.ResultLimit => output + "[glob results truncated: result limit reached]\n",
                WalkStopReason.VisitLimit => output + "[glob results truncated: visit limit reached]\n",
                _ => output,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "error: glob timed out";
        }
    }

    private static WalkStopReason Walk(
        ToolWorkspace workspace,
        (string Lexical, string Physical) directory,
        string relativeDirectory,
        Regex regex,
        List<string> results,
        ref int visited,
        SecurityProfile securityProfile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string[] entries;

        try
        {
            entries = [.. Directory.EnumerateFileSystemEntries(directory.Physical).Order(StringComparer.Ordinal)];
        }
        catch (IOException)
        {
            return WalkStopReason.Completed;
        }
        catch (UnauthorizedAccessException)
        {
            return WalkStopReason.Completed;
        }

        foreach (var entry in entries)
        {
            if (results.Count > MaxResults)
            {
                return WalkStopReason.ResultLimit;
            }

            if (visited >= MaxVisited)
            {
                return WalkStopReason.VisitLimit;
            }

            visited++;
            var name = Path.GetFileName(entry);
            var relative = relativeDirectory.Length == 0
                ? name
                : $"{relativeDirectory}/{name}";
            var lexical = Path.Combine(directory.Lexical, name);
            (string Lexical, string Physical) resolved;

            try
            {
                resolved = workspace.ResolveRead(lexical);
            }
            catch (Exception failure) when (failure is InvalidOperationException or IOException)
            {
                continue;
            }

            if (!ToolWorkspace.AllowsRead(resolved, securityProfile))
            {
                continue;
            }

            FileAttributes attributes;

            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            var isSymlink = (attributes & FileAttributes.ReparsePoint) != 0;
            var isRealDirectory = (attributes & FileAttributes.Directory) != 0 && !isSymlink;

            if (regex.IsMatch(relative))
            {
                results.Add(isRealDirectory || (isSymlink && Directory.Exists(entry)) ? relative + "/" : relative);
                if (results.Count > MaxResults)
                {
                    return WalkStopReason.ResultLimit;
                }
            }

            if (isRealDirectory)
            {
                var stopReason = Walk(
                    workspace,
                    resolved,
                    relative,
                    regex,
                    results,
                    ref visited,
                    securityProfile,
                    cancellationToken);
                if (stopReason != WalkStopReason.Completed)
                {
                    return stopReason;
                }
            }
        }

        return WalkStopReason.Completed;
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

    internal sealed class Input
    {
        [JsonPropertyName("pattern")]
        public string? Pattern { get; init; }

        [JsonPropertyName("path")]
        public string? Path { get; init; }
    }
}
