using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Tools;

internal sealed class ClearExitReminderTool(IExitReminder reminder, IPromptTemplateCatalog promptTemplates) : ITool
{
    public string Name => "clear_exit_reminder";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(
                invocation.ArgumentsJson,
                AgentProcessToolJsonContext.Default.ClearExitReminderToolInput);
            if (input is not { Title.Length: > 0 })
            {
                return ToolResultFormatter.Error(invocation, "clear_exit_reminder requires a nonempty title.");
            }

            var title = new PromptTemplateArgument("title", input.Title);
            return await reminder.Clear(input.Title, cancellationToken).ConfigureAwait(false)
                ? ToolResultFormatter.Text(invocation, promptTemplates.Render("clear-exit-reminder-tool.cleared", [title]))
                : ToolResultFormatter.Error(
                    invocation,
                    promptTemplates.Render(
                        "clear-exit-reminder-tool.not-found",
                        [title, new PromptTemplateArgument("titles", string.Join(", ", reminder.Titles))]));
        }
        catch (Exception failure) when (failure is JsonException or FormatException or ArgumentException or Store.InputConflictException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("title")]
        public string? Title { get; init; }
    }
}
