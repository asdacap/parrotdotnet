using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Parrot.Security;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class GrepTool(ToolWorkspace workspace, SecurityProfile securityProfile) : ITool
{
    private const int MaxMatches = 1000;
    private const int MaxLineLength = 512;
    private const int MaxFiles = 10_000;
    private const int BinaryProbeSize = 8192;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public string Name => "grep";

    public string Description =>
        "Search text files with .NET non-backtracking regular expressions. Relative paths resolve within the workspace.";

    public string ParametersJson => Input.Descriptor;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        string pattern;
        string path;

        try
        {
            var input = JsonSerializer.Deserialize(argumentsJson, FileToolJsonContext.Default.GrepToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            pattern = input.Pattern ?? throw new FormatException("Tool arguments require a string 'pattern'.");
            path = input.Path ?? string.Empty;
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

        (string Lexical, string Physical) resolved;

        try
        {
            resolved = workspace.ResolveRead(path.Length == 0 ? "." : path);
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException)
        {
            return $"error: {failure.Message}";
        }

        if (!securityProfile.AllowsRead(resolved.Lexical) || !securityProfile.AllowsRead(resolved.Physical))
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
                await SearchFile(workspace.Root, full, regex, state, securityProfile, timeoutCancellation.Token)
                    .ConfigureAwait(false);
            }
            else if (Directory.Exists(full))
            {
                await SearchDirectory(
                    workspace,
                    workspace.Root,
                    resolved,
                    regex,
                    state,
                    securityProfile,
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
        string root,
        (string Lexical, string Physical) directory,
        Regex regex,
        GrepState state,
        SecurityProfile securityProfile,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        CollectFiles(workspace, directory, files, securityProfile, cancellationToken);
        files.Sort(StringComparer.Ordinal);

        foreach (var file in files)
        {
            if (state.Matches >= MaxMatches)
            {
                return;
            }

            await SearchFile(root, file, regex, state, securityProfile, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void CollectFiles(
        ToolWorkspace workspace,
        (string Lexical, string Physical) directory,
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

            if (!securityProfile.AllowsRead(resolved.Lexical) || !securityProfile.AllowsRead(resolved.Physical))
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
                CollectFiles(workspace, resolved, files, securityProfile, cancellationToken);
            }
            else
            {
                files.Add(resolved.Physical);
            }

            if (files.Count >= MaxFiles)
            {
                return;
            }
        }
    }

    private static async Task SearchFile(
        string root,
        string file,
        Regex regex,
        GrepState state,
        SecurityProfile securityProfile,
        CancellationToken cancellationToken)
    {
        if (!securityProfile.AllowsRead(file) || IsBinary(file))
        {
            return;
        }

        var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
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

            _ = state.Output.Append(relative).Append(':')
                .Append(lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(':').Append(display).Append('\n');
        }
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
        [Description("Optional workspace-relative file or directory to search.")]
        public string? Path { get; init; }
    }

    private sealed class GrepState
    {
        public StringBuilder Output { get; } = new();

        public int Matches { get; set; }
    }
}
