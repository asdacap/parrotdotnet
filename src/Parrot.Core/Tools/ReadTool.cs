using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Security;

namespace Parrot.Tools;

internal sealed class ReadTool(ToolWorkspace workspace) : ITool
{
    private const int MaxLines = 2000;
    private const int MaxOutputBytes = 1 << 20;
    private const int BinaryProbeSize = 8192;

    public string Name => "read";

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

        if (!ToolWorkspace.AllowsRead(resolved, selection.SecurityProfile))
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
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var probe = new byte[BinaryProbeSize];
        var probeRead = await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false);

        if (probe.AsSpan(0, probeRead).Contains((byte)0))
        {
            return "error: binary file";
        }

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

        _ = output.Append("total lines in file: ")
            .Append(lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append('\n');
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

            if (!ToolWorkspace.AllowsRead(resolved, securityProfile))
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

    internal sealed class Input
    {
        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("offset")]
        public int? Offset { get; init; }

        [JsonPropertyName("limit")]
        public int? Limit { get; init; }
    }
}
