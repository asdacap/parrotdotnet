using System.Globalization;
using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class QueueTakeToolPresenter : IToolPresenter
{
    public string ToolName => "queue_take";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var input = QueueTakeInput.Parse(call.ArgumentsJson);
        return new ToolLiveValue(Label(call.Owner, input.Name, input.Count), ToolBlock.Empty, Metadata, frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var input = QueueTakeInput.Parse(call.ArgumentsJson);
        var status = terminal.ResolveStatus();
        if (status != ToolTerminalStatus.Succeeded)
        {
            return new ToolScrollbackValue(
                Label(call.Owner, input.Name, input.Count),
                terminal.DescribeBlock(ToolBlockKind.None),
                status,
                Metadata);
        }

        var result = QueueTakeResult.Parse(terminal.Result);
        return new ToolScrollbackValue(
            Label(call.Owner, result.Name, input.Count, result.Description, result.Closed),
            ToolBlock.FromQueue([string.Concat(result.Size.ToString(CultureInfo.InvariantCulture), " remaining"), .. result.Items]),
            status,
            Metadata);
    }

    private static string Label(string owner, string name, int count)
    {
        var unit = count == 1 ? "item" : "items";
        return $"{owner}: Take from queue {name} · up to {count.ToString(CultureInfo.InvariantCulture)} {unit}";
    }

    private static string Label(string owner, string name, int count, string description, bool closed)
    {
        var label = description.Length == 0
            ? Label(owner, name, count)
            : $"{Label(owner, name, count)} · {description}";
        return closed ? $"{label} · closed" : label;
    }

    private readonly record struct QueueTakeInput(string Name, int Count)
    {
        public static QueueTakeInput Parse(string argumentsJson)
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(name.GetString()))
            {
                throw new FormatException("Queue take arguments require a name.");
            }

            var countValue = 1;
            if (root.TryGetProperty("count", out var count)
                && (!count.TryGetInt32(out countValue) || countValue < 1))
            {
                throw new FormatException("Queue take count must be a positive integer.");
            }

            var nameValue = name.GetString() ?? throw new FormatException("Queue take arguments require a name.");
            return new QueueTakeInput(nameValue, countValue);
        }
    }

    private readonly record struct QueueTakeResult(
        string Name,
        string Description,
        int Size,
        bool Closed,
        string[] Items)
    {
        public static QueueTakeResult Parse(string resultJson)
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(name.GetString())
                || !root.TryGetProperty("size", out var size)
                || !size.TryGetInt32(out var sizeValue)
                || sizeValue < 0
                || !root.TryGetProperty("closed", out var closed)
                || (closed.ValueKind != JsonValueKind.True && closed.ValueKind != JsonValueKind.False)
                || !root.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Queue take result requires a name, size, closed state, and items.");
            }

            var description = string.Empty;
            if (root.TryGetProperty("description", out var descriptionValue))
            {
                if (descriptionValue.ValueKind == JsonValueKind.Null)
                {
                    description = string.Empty;
                }
                else if (descriptionValue.ValueKind == JsonValueKind.String)
                {
                    description = descriptionValue.GetString() ?? string.Empty;
                }
                else
                {
                    throw new FormatException("Queue take result description must be a string.");
                }
            }

            var values = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new FormatException("Queue take result items must be strings.");
                }

                values.Add(item.GetString() ?? string.Empty);
            }

            var nameValue = name.GetString() ?? throw new FormatException("Queue take result requires a name, size, closed state, and items.");
            return new QueueTakeResult(nameValue, description, sizeValue, closed.GetBoolean(), [.. values]);
        }
    }
}
