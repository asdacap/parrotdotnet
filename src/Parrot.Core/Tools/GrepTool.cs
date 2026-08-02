using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Parrot.Agent;
using Parrot.Security;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class GrepTool(ToolWorkspace workspace) : ITool
{
    private const int MaxMatches = 1000;
    private const int MaxLineLength = 512;
    private const int MaxFiles = 10_000;
    private const int BinaryProbeSize = 8192;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public string Name => "grep";

    public string Description =>
        "Search text files with .NET non-backtracking regular expressions. "
        + "Relative paths resolve within the workspace; absolute paths require read permission.";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        string pattern;
        string path;
        string? include;

        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, FileToolJsonContext.Default.GrepToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            pattern = input.Pattern ?? throw new FormatException("Tool arguments require a string 'pattern'.");
            path = input.Path ?? string.Empty;
            include = input.Include;
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return $"error: {failure.Message}";
        }

        if (pattern.Length == 0)
        {
            return "error: grep pattern must not be empty";
        }

        Regex regex;
        Regex? includeRegex;

        try
        {
            regex = new Regex(
                pattern,
                RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                Timeout);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException)
        {
            return $"error: invalid regular expression: {failure.Message}";
        }

        if (include is { Length: 0 } || include?.Contains('\0', StringComparison.Ordinal) is true)
        {
            return "error: include glob must be a non-empty string without NUL";
        }

        if (include is not null && Path.IsPathFullyQualified(include))
        {
            return "error: include glob must be a relative path";
        }

        try
        {
            includeRegex = include is null
                ? null
                : new Regex(
                    ConvertGlobToRegex(include.Contains('/', StringComparison.Ordinal) ? include : $"**/{include}"),
                    RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                    Timeout);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException)
        {
            return $"error: invalid include glob: {failure.Message}";
        }

        (string Lexical, string Physical) resolved;

        try
        {
            resolved = workspace.ResolveRead(path.Length == 0 ? "." : path);
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException)
        {
            return $"error: {failure.Message}";
        }

        if (!workspace.AllowsRead(resolved, selection.SecurityProfile))
        {
            return "error: access denied";
        }

        var full = resolved.Physical;

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(Timeout);

        try
        {
            var state = new GrepState();

            if (File.Exists(full))
            {
                var displayPath = Path.GetFileName(resolved.Lexical);
                await SearchFile(workspace, full, displayPath, regex, state, selection.SecurityProfile, timeoutCancellation.Token)
                    .ConfigureAwait(false);
            }
            else if (Directory.Exists(full))
            {
                await SearchDirectory(
                    workspace,
                    resolved,
                    regex,
                    includeRegex,
                    state,
                    selection.SecurityProfile,
                    timeoutCancellation.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                return "error: no such file or directory";
            }

            if (state.Matches == 0)
            {
                return string.Empty;
            }

            var truncated = state.Matches >= MaxMatches;
            return truncated
                ? state.Output.ToString() + "[grep results truncated]\n"
                : state.Output.ToString();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "error: grep timed out";
        }
    }

    private static async Task SearchDirectory(
        ToolWorkspace workspace,
        (string Lexical, string Physical) directory,
        Regex regex,
        Regex? includeRegex,
        GrepState state,
        SecurityProfile securityProfile,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        CollectFiles(workspace, directory, directory.Physical, includeRegex, files, securityProfile, cancellationToken);
        files.Sort(StringComparer.Ordinal);

        foreach (var file in files)
        {
            if (state.Matches >= MaxMatches)
            {
                return;
            }

            var displayPath = Path.GetRelativePath(directory.Physical, file).Replace(Path.DirectorySeparatorChar, '/');
            await SearchFile(workspace, file, displayPath, regex, state, securityProfile, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void CollectFiles(
        ToolWorkspace workspace,
        (string Lexical, string Physical) directory,
        string searchRoot,
        Regex? includeRegex,
        List<string> files,
        SecurityProfile securityProfile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (files.Count >= MaxFiles)
        {
            return;
        }

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
            var lexical = Path.Combine(directory.Lexical, Path.GetFileName(entry));
            (string Lexical, string Physical) resolved;

            try
            {
                resolved = workspace.ResolveRead(lexical);
            }
            catch (Exception failure) when (failure is InvalidOperationException or IOException)
            {
                continue;
            }

            if (!workspace.AllowsRead(resolved, securityProfile))
            {
                continue;
            }

            var attributes = File.GetAttributes(entry);

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                CollectFiles(workspace, resolved, searchRoot, includeRegex, files, securityProfile, cancellationToken);
            }
            else
            {
                var relative = Path.GetRelativePath(searchRoot, resolved.Physical)
                    .Replace(Path.DirectorySeparatorChar, '/');

                if (includeRegex is null || includeRegex.IsMatch(relative))
                {
                    files.Add(resolved.Physical);
                }
            }

            if (files.Count >= MaxFiles)
            {
                return;
            }
        }
    }

    private static async Task SearchFile(
        ToolWorkspace workspace,
        string file,
        string displayPath,
        Regex regex,
        GrepState state,
        SecurityProfile securityProfile,
        CancellationToken cancellationToken)
    {
        if (!workspace.AllowsRead(workspace.ResolveRead(file), securityProfile) || IsBinary(file))
        {
            return;
        }

        var lineNumber = 0;

        using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                return;
            }

            lineNumber++;

            if (!regex.IsMatch(line))
            {
                continue;
            }

            state.Matches++;

            if (state.Matches > MaxMatches)
            {
                return;
            }

            var display = line.Length > MaxLineLength
                ? string.Concat(line.AsSpan(0, MaxLineLength), " [truncated]")
                : line;

            _ = state.Output.Append(displayPath).Append(':')
                .Append(lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(':').Append(display).Append('\n');
        }
    }

    private static string ConvertGlobToRegex(string pattern)
    {
        var regex = new StringBuilder("^");
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

    private static bool IsBinary(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var probe = new byte[BinaryProbeSize];
            var read = stream.Read(probe);
            return probe.AsSpan(0, read).Contains((byte)0);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [JsonPropertyName("pattern")]
        [Description(".NET non-backtracking regular expression to search for.")]
        [ToolRequired]
        public string? Pattern { get; init; }

        [JsonPropertyName("path")]
        [Description("Optional workspace-relative or authorized absolute file or directory to search.")]
        public string? Path { get; init; }

        [JsonPropertyName("include")]
        [Description("Optional root-relative glob pattern restricting searched files, including **.")]
        public string? Include { get; init; }
    }

    private sealed class GrepState
    {
        public StringBuilder Output { get; } = new();

        public int Matches { get; set; }
    }
}
