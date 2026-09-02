using Parrot.Config;

namespace Parrot.Tools;

internal static class ToolResultFormatter
{
    public static string Text(ToolInvocation invocation, string value) => invocation.PromptTemplates is { } templates
        ? templates.Render("tool-result.text", [new PromptTemplateArgument("value", value)])
        : value;

    public static string Error(ToolInvocation invocation, string message) => invocation.PromptTemplates is { } templates
        ? templates.Render("tool-result.error", [new PromptTemplateArgument("message", message)])
        : $"error: {message}";

    public static string QuestionAnswer(ToolInvocation invocation, string question, string answer) => invocation.PromptTemplates is { } templates
        ? templates.Render("tool-result.question-answer", [new PromptTemplateArgument("question", question), new PromptTemplateArgument("answer", answer)])
        : $"Question: {question}\nAnswer: {answer}";

    public static string Marker(ToolInvocation invocation, string marker) => Text(invocation, marker);
}
