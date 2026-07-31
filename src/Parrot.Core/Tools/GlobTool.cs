using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Parrot.Security;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class GlobTool(ToolWorkspace workspace, SecurityProfile securityProfile) : ITool
{
    private const int MaxResults = 1000;
    private const int MaxVisited = 100_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public string Name => "glob";

    public string Description =>
        "Find paths beneath an optional root with deterministic glob matching, including **. "
        + "Relative roots resolve within the workspace.";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(ToolInvocation invocation, CancellationToken cancellationToken)
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

        if (!securityProfile.AllowsRead(root.Lexical) || !securityProfile.AllowsRead(root.Physical))
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
            Walk(
                workspace,
                root,
                string.Empty,
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

            if (!securityProfile.AllowsRead(resolved.Lexical) || !securityProfile.AllowsRead(resolved.Physical))
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
            }

            if (isRealDirectory)
            {
                Walk(workspace, resolved, relative, regex, results, ref visited, securityProfile, cancellationToken);
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

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [JsonPropertyName("pattern")]
        [Description("Root-relative glob pattern, including ** for recursive matching.")]
        [ToolRequired]
        public string? Pattern { get; init; }

        [JsonPropertyName("path")]
        [Description("Optional workspace-relative or authorized absolute directory to search.")]
        public string? Path { get; init; }
    }
}
