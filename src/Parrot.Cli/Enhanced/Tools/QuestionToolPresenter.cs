using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class QuestionToolPresenter : IToolPresenter
{
    public string ToolName => "question";

    public ToolPresentationMetadata Metadata => new(ToolPresentationStyle.Default, string.Empty, false, false, true, [], false);

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var questions = document.RootElement.GetProperty("questions");
        var prompt = questions.GetArrayLength() > 0
            ? questions[0].GetProperty("prompt").GetString() ?? "Question"
            : "Question";
        return new ToolLiveValue(
            $"{call.Owner}: Question · {questions.GetArrayLength()} {(questions.GetArrayLength() == 1 ? "item" : "items")}",
            [prompt],
            Metadata,
            frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        var questions = arguments.RootElement.GetProperty("questions");
        var details = questions.EnumerateArray().Select(question =>
            question.GetProperty("prompt").GetString() ?? "Question").ToArray();
        return new ToolScrollbackValue(
            $"{call.Owner}: Question · {details.Length} {(details.Length == 1 ? "item" : "items")}",
            terminal.DescribeBlock(ToolBlockKind.Text),
            terminal.ResolveStatus(),
            Metadata);
    }
}
