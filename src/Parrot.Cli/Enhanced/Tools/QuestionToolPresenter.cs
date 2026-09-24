using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class QuestionToolPresenter : IToolPresenter
{
    public string ToolName => "question";

    public ToolPresentationMetadata Metadata => new(ToolPresentationStyle.Default, string.Empty, false, false, true, [], false, false);

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var questions = document.RootElement.GetProperty("questions");
        var prompt = questions.GetArrayLength() > 0
            ? questions[0].GetProperty("prompt").GetString() ?? "Question"
            : "Question";
        return new ToolLiveValue(
            $"Question · {questions.GetArrayLength()} {(questions.GetArrayLength() == 1 ? "item" : "items")}",
            [prompt],
            Metadata,
            frame);
    }

    public IScrollbackItem PresentChildStarted(ToolCallPresentation call)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        var questions = arguments.RootElement.GetProperty("questions");
        var details = questions.EnumerateArray().SelectMany(question =>
            new[] { question.GetProperty("prompt").GetString() ?? "Question" }.Concat(
                question.TryGetProperty("options", out var options)
                    ? options.EnumerateArray().Select(option => $"  - {option.GetProperty("label").GetString()}")
                    : [])).ToArray();
        return new ToolScrollbackValue(
            $"Question · {questions.GetArrayLength()} {(questions.GetArrayLength() == 1 ? "item" : "items")}",
            details,
            ToolTerminalStatus.Succeeded,
            Metadata with { Style = ToolPresentationStyle.Muted, SuccessIcon = TerminalIcons.Pending });
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        var questions = arguments.RootElement.GetProperty("questions");
        var details = questions.EnumerateArray().Select(question =>
            question.GetProperty("prompt").GetString() ?? "Question").ToArray();
        return new ToolScrollbackValue(
            $"Question · {details.Length} {(details.Length == 1 ? "item" : "items")}",
            terminal.DescribeBlock(ToolBlockKind.Text),
            terminal.ResolveStatus(),
            Metadata);
    }
}
