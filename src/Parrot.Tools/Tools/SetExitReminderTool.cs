using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Tools;

internal sealed class SetExitReminderTool(IExitReminder reminder, IPromptTemplateCatalog promptTemplates) : ITool
{
    public string Name => "set_exit_reminder";

    public Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(
                invocation.ArgumentsJson,
                AgentProcessToolJsonContext.Default.SetExitReminderToolInput);
            reminder.Set(input?.Reminder);
            var response = string.IsNullOrEmpty(input?.Reminder)
                ? promptTemplates.Render("set-exit-reminder-tool.cleared", [])
                : promptTemplates.Render(
                    "set-exit-reminder-tool.set",
                    [new PromptTemplateArgument("reminder", input.Reminder)]);
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Text(invocation, response));
        }
        catch (Exception failure) when (failure is JsonException or FormatException or ArgumentException or Store.InputConflictException)
        {
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Error(invocation, failure.Message));
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("reminder")]
        public string? Reminder { get; init; }
    }
}
