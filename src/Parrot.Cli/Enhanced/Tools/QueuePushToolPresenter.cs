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
            Label(call.Owner, input.Name, input.Close ? QueueState.Closed : QueueState.Open),
            ToolBlock.FromQueue(input.Items),
            Metadata,
            frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var input = QueuePushInput.Parse(call.ArgumentsJson);
        var status = terminal.ResolveStatus();
        var state = ResolveState(input, terminal, status);
        var block = status == ToolTerminalStatus.Succeeded
            ? ToolBlock.FromQueue(input.Items)
            : terminal.DescribeBlock(ToolBlockKind.None);
        return new ToolScrollbackValue(Label(call.Owner, input.Name, state), block, status, Metadata);
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

    private static string Label(string owner, string name, QueueState state) =>
        $"{owner}: Push to queue {name} · {state.ToString().ToLowerInvariant()}";

    private readonly record struct QueuePushInput(string Name, string[] Items, bool Close)
    {
        public static QueuePushInput Parse(string argumentsJson)
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(name.GetString())
                || !root.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Queue push arguments require a name and items.");
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
                    : throw new FormatException("Queue push arguments require a name and items."),
                [.. values],
                close);
        }
    }
}
