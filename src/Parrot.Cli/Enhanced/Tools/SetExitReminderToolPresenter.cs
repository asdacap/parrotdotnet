using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class SetExitReminderToolPresenter : IToolPresenter
{
    public string ToolName => "set_exit_reminder";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var input = SetExitReminderInput.Parse(call.ArgumentsJson);
        return new ToolLiveValue(Label(input), ToolBlock.Empty, Metadata, frame);
    }

    public IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var status = terminal.ResolveStatus();
        return status == ToolTerminalStatus.Succeeded
            ? null
            : new ToolScrollbackValue(
                Label(SetExitReminderInput.Parse(call.ArgumentsJson)),
                terminal.DescribeBlock(ToolBlockKind.None),
                status,
                Metadata);
    }

    private static string Label(SetExitReminderInput input) => $"Set exit reminder · {input.Title} · {input.Description}";

    private readonly record struct SetExitReminderInput(string Title, string Description)
    {
        public static SetExitReminderInput Parse(string argumentsJson)
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("title", out var title)
                || title.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("description", out var description)
                || description.ValueKind != JsonValueKind.String)
            {
                throw new FormatException("set_exit_reminder requires a string title and description.");
            }

            return new SetExitReminderInput(title.GetString() ?? string.Empty, description.GetString() ?? string.Empty);
        }
    }
}
