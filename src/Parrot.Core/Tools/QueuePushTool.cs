using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Queues;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class QueuePushTool(AgentQueues queues, ToolWorkspace workspace) : ITool
{
    private const int MaximumSourceFileBytes = 16 << 20;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string Name => "queue_push";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = QueueToolExecution.Deserialize(
                invocation.ArgumentsJson,
                QueueToolJsonContext.Default.QueuePushToolInput);
            var items = await LoadItems(input, selection, cancellationToken).ConfigureAwait(false);
            var info = await queues.Push(
                QueueToolExecution.RequireName(input.Name),
                items,
                QueueToolExecution.ParseDirection(input.Direction),
                input.Close ?? false,
                cancellationToken).ConfigureAwait(false);
            return QueueToolExecution.Serialize(info);
        }
        catch (Exception failure) when (failure is JsonException or FormatException or QueueException)
        {
            return $"error: {failure.Message}";
        }
    }

    private static async Task<byte[]> ReadSourceFile(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumSourceFileBytes)
        {
            throw new FormatException($"source_file exceeds {MaximumSourceFileBytes} bytes");
        }

        using var output = new MemoryStream(Math.Min((int)stream.Length, MaximumSourceFileBytes));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > MaximumSourceFileBytes)
            {
                throw new FormatException($"source_file exceeds {MaximumSourceFileBytes} bytes");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string DecodeSourceFile(byte[] data)
    {
        var content = data.AsSpan();
        ReadOnlySpan<byte> byteOrderMark = [0xEF, 0xBB, 0xBF];
        if (content.StartsWith(byteOrderMark))
        {
            content = content[byteOrderMark.Length..];
        }

        try
        {
            return StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException failure)
        {
            throw new FormatException("source_file is not valid UTF-8", failure);
        }
    }

    private async Task<IReadOnlyList<string>> LoadItems(
        Input input,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        var hasItems = input.Items is not null;
        var hasSourceFile = input.SourceFile is not null;
        if (hasItems == hasSourceFile)
        {
            throw new FormatException("Tool arguments require exactly one of 'items' or 'source_file'.");
        }

        if (input.Items is not null)
        {
            return input.Items;
        }

        var sourceFile = input.SourceFile;
        if (string.IsNullOrEmpty(sourceFile))
        {
            throw new FormatException("Tool argument 'source_file' must be a non-empty string.");
        }

        (string Lexical, string Physical) resolved;
        try
        {
            resolved = workspace.ResolveRead(sourceFile);
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException)
        {
            throw new FormatException(failure.Message, failure);
        }

        if (!ToolWorkspace.AllowsRead(resolved, selection.SecurityProfile))
        {
            throw new FormatException("access denied");
        }

        if (Directory.Exists(resolved.Physical))
        {
            throw new FormatException("source_file must be a file");
        }

        if (!File.Exists(resolved.Physical))
        {
            throw new FormatException("no such file or directory");
        }

        byte[] data;
        try
        {
            data = await ReadSourceFile(resolved.Physical, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException failure)
        {
            throw new FormatException("no such file or directory", failure);
        }
        catch (DirectoryNotFoundException failure)
        {
            throw new FormatException("no such file or directory", failure);
        }
        catch (UnauthorizedAccessException failure)
        {
            throw new FormatException("access denied", failure);
        }
        catch (IOException failure)
        {
            throw new FormatException("could not read source_file", failure);
        }

        if (data.AsSpan().Contains((byte)0))
        {
            throw new FormatException("binary file");
        }

        var content = DecodeSourceFile(data);
        var items = new List<string>();
        using var reader = new StringReader(content);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                items.Add(line);
            }
        }

        return items;
    }

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [JsonPropertyName("name")]
        [ToolPattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
        [ToolRequired]
        public string? Name { get; init; }

        [JsonPropertyName("items")]
        public string[]? Items { get; init; }

        [JsonPropertyName("source_file")]
        public string? SourceFile { get; init; }

        [JsonPropertyName("direction")]
        [ToolDefaultString("back")]
        [ToolStringEnum("front", "back")]
        public string? Direction { get; init; }

        [JsonPropertyName("close")]
        [ToolDefaultBool(false)]
        public bool? Close { get; init; }
    }
}
