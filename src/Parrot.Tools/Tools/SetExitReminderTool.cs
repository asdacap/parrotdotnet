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
            if (input is not { Title.Length: > 0, Description.Length: > 0 })
            {
                return ToolResultFormatter.Error(invocation, "set_exit_reminder requires a nonempty title and description.");
            }

            await reminder.Set(input.Title, input.Description, cancellationToken).ConfigureAwait(false);
            var response = promptTemplates.Render(
                "set-exit-reminder-tool.set",
                [
                    new PromptTemplateArgument("title", input.Title),
                    new PromptTemplateArgument("description", input.Description),
                ]);
            return ToolResultFormatter.Text(invocation, response);
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

        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }
}
