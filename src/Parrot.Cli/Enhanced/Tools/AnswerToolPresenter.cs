using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class AnswerToolPresenter : IToolPresenter
{
    public string ToolName => "answer";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var input = AnswerInput.Parse(call);
        return new ToolLiveValue(input.Label, input.Block, Metadata, frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var input = AnswerInput.Parse(call);
        var status = terminal.ResolveStatus();
        return new ToolScrollbackValue(
            input.Label,
            status == ToolTerminalStatus.Succeeded ? input.Block : terminal.DescribeBlock(ToolBlockKind.None),
            status,
            Metadata);
    }

    private readonly record struct AnswerInput(string Recipient, string[] Answers)
    {
        public string Label =>
            $"Answer · {Recipient} · {Answers.Length} {(Answers.Length == 1 ? "item" : "items")}";

        public ToolBlock Block => Answers.Length == 1
            ? ToolBlock.FromText(Answers[0])
            : ToolBlock.FromText(string.Join('\n', Answers.Select(Number)));

        public static AnswerInput Parse(ToolCallPresentation call)
        {
            using var arguments = JsonDocument.Parse(call.ArgumentsJson);
            var root = arguments.RootElement;
            var agentName = root.GetProperty("agent_name").GetString() ?? string.Empty;
            var answers = root.TryGetProperty("answers", out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(answer => answer.GetString() ?? string.Empty).ToArray()
                : [];
            return new AnswerInput(call.ResolveAgentReference(agentName), answers);
        }

        private static string Number(string answer, int index)
        {
            var prefix = $"{index + 1}. ";
            var indent = new string(' ', prefix.Length);
            return prefix + answer.Replace("\n", $"\n{indent}", StringComparison.Ordinal);
        }
    }
}
