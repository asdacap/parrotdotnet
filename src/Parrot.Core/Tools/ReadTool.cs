using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Security;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class ReadTool(ToolWorkspace workspace) : ITool
{
    private const int MaxLines = 2000;
    private const int MaxOutputBytes = 1 << 20;
    private const int BinaryProbeSize = 8192;

    public string Name => "read";

    public string Description =>
        "Read a bounded line range from a text file or list a directory. "
        + "Relative paths resolve within the workspace; absolute paths require read permission.";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        string path;
        int offset;
        int limit;

        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, FileToolJsonContext.Default.ReadToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            path = input.Path ?? throw new FormatException("Tool arguments require a string 'path'.");
            offset = input.Offset ?? 1;
            limit = input.Limit ?? MaxLines;
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return $"error: {failure.Message}";
        }

        if (offset < 1)
        {
            return "error: offset must be at least 1";
        }

        if (limit is < 1 or > MaxLines)
        {
            return $"error: limit must be between 1 and {MaxLines}";
        }

        (string Lexical, string Physical) resolved;

        try
        {
            resolved = workspace.ResolveRead(path);
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException)
        {
            return $"error: {failure.Message}";
        }

        if (!workspace.AllowsRead(resolved, selection.SecurityProfile))
        {
            return "error: access denied";
        }

        if (Directory.Exists(resolved.Physical))
        {
            return ListDirectory(workspace, resolved, selection.SecurityProfile);
        }

        return !File.Exists(resolved.Physical)
            ? "error: no such file or directory"
            : await ReadFile(resolved.Physical, offset, limit, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadFile(
        string full, int offset, int limit, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(full);

        var probe = new byte[BinaryProbeSize];
        var probeRead = await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false);

        if (probe.AsSpan(0, probeRead).Contains((byte)0))
        {
            return "error: binary file";
        }

        _ = stream.Seek(0, SeekOrigin.Begin);
        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        _ = stream.Seek(0, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var output = new StringBuilder();
        var outputBytes = 0;
        var lineNumber = 0;
        var truncated = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                break;
            }

            lineNumber++;

            if (lineNumber < offset || lineNumber >= offset + limit || truncated)
            {
                continue;
            }

            var formatted = string.Concat(
                lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), ": ", line, "\n");
            var formattedBytes = Encoding.UTF8.GetByteCount(formatted);

            if (outputBytes + formattedBytes > MaxOutputBytes)
            {
                truncated = true;
                continue;
            }

            _ = output.Append(formatted);
            outputBytes += formattedBytes;
        }

        if (truncated)
        {
            _ = output.Append("[output truncated]\n");
        }

        _ = output.Append("sha256: ").Append(Convert.ToHexString(hashBytes).ToLowerInvariant()).Append('\n');
        return output.ToString();
    }

    private static string ListDirectory(
        ToolWorkspace workspace,
        (string Lexical, string Physical) directory,
        SecurityProfile securityProfile)
    {
        var output = new StringBuilder();
        var count = 0;

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Physical).Order(StringComparer.Ordinal))
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

            if (count >= MaxLines || output.Length >= MaxOutputBytes)
            {
                _ = output.Append("[listing truncated]\n");
                break;
            }

            var name = Path.GetFileName(entry);
            var isDirectory = Directory.Exists(entry);
            _ = output.Append(name);

            if (isDirectory)
            {
                _ = output.Append('/');
            }

            _ = output.Append('\n');
            count++;
        }

        return output.ToString();
    }

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [JsonPropertyName("path")]
        [Description("Workspace-relative or authorized absolute file or directory path.")]
        [ToolRequired]
        public string? Path { get; init; }

        [JsonPropertyName("offset")]
        [Description("One-based first line to read.")]
        [ToolMinimum(1)]
        public int? Offset { get; init; }

        [JsonPropertyName("limit")]
        [Description("Maximum number of lines to read.")]
        [ToolMinimum(1)]
        public int? Limit { get; init; }
    }
}
