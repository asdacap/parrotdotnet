using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class QueuePushToolPresenter : IToolPresenter
{
    private enum QueueState
    {
        Open,
        Closed,
    }

    public string ToolName => "queue_push";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var input = QueuePushInput.Parse(call.ArgumentsJson);
        return new ToolLiveValue(
            Label(input.Name, input.Close ? QueueState.Closed : QueueState.Open),
            ToolBlock.FromQueue(input.Details),
            Metadata,
            frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var input = QueuePushInput.Parse(call.ArgumentsJson);
        var status = terminal.ResolveStatus();
        var state = ResolveState(input, terminal, status);
        var block = status == ToolTerminalStatus.Succeeded
            ? ToolBlock.FromQueue(input.Details)
            : terminal.DescribeBlock(ToolBlockKind.None);
        return new ToolScrollbackValue(Label(input.Name, state), block, status, Metadata);
    }

    private static QueueState ResolveState(
        QueuePushInput input,
        ToolTerminalPresentation terminal,
        ToolTerminalStatus status) =>
        status == ToolTerminalStatus.Succeeded
            ? input.Close ? QueueState.Closed : QueueState.Open
            : IsClosedQueueFailure(terminal) ? QueueState.Closed : QueueState.Open;

    private static bool IsClosedQueueFailure(ToolTerminalPresentation terminal) =>
        terminal.ResultPresent
        && terminal.Result.StartsWith("error: queue: '", StringComparison.Ordinal)
        && terminal.Result.Contains("' is closed", StringComparison.Ordinal);

    private static string Label(string name, QueueState state) =>
        $"Push to queue {name} · {state.ToString().ToLowerInvariant()}";

    private readonly record struct QueuePushInput(string Name, string[] Details, bool Close)
    {
        public static QueuePushInput Parse(string argumentsJson)
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(name.GetString()))
            {
                throw new FormatException("Queue push arguments require a name and exactly one item source.");
            }

            var hasItems = root.TryGetProperty("items", out var items);
            var hasSourceFile = root.TryGetProperty("source_file", out var sourceFile);
            if (hasItems == hasSourceFile)
            {
                throw new FormatException("Queue push arguments require exactly one of items or source_file.");
            }

            string[] details;
            if (hasItems)
            {
                if (items.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException("Queue push items must be an array.");
                }

                var values = new List<string>();
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        throw new FormatException("Queue push items must be strings.");
                    }

                    values.Add(item.GetString() ?? string.Empty);
                }

                details = [.. values];
            }
            else
            {
                if (sourceFile.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(sourceFile.GetString()))
                {
                    throw new FormatException("Queue push source_file must be a non-empty string.");
                }

                details = [$"source_file: {sourceFile.GetString()}"];
            }

            var close = false;
            if (root.TryGetProperty("close", out var closeValue))
            {
                close = closeValue.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new FormatException("Queue push close must be a boolean."),
                };
            }

            var nameValue = name.GetString();
            return new QueuePushInput(
                !string.IsNullOrEmpty(nameValue)
                    ? nameValue
                    : throw new FormatException("Queue push arguments require a name and exactly one item source."),
                details,
                close);
        }
    }
}
