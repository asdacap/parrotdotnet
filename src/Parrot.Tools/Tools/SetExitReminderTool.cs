using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Tools;

internal sealed class SetExitReminderTool(IExitReminder reminder, IPromptTemplateCatalog promptTemplates) : ITool
{
    public string Name => "set_exit_reminder";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(
                invocation.ArgumentsJson,
                AgentProcessToolJsonContext.Default.SetExitReminderToolInput);
            await reminder.Set(input?.Reminder, cancellationToken).ConfigureAwait(false);
            var response = input?.Reminder is null or "" or "null" or "undefined"
                ? promptTemplates.Render("set-exit-reminder-tool.cleared", [])
                : promptTemplates.Render(
                    "set-exit-reminder-tool.set",
                    [new PromptTemplateArgument("reminder", input.Reminder)]);
            return ToolResultFormatter.Text(invocation, response);
        }
        catch (Exception failure) when (failure is JsonException or FormatException or ArgumentException or Store.InputConflictException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("reminder")]
        public string? Reminder { get; init; }
    }
}
