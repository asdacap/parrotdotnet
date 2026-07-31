using System.Text.Json;
using Parrot.Agent;
using Parrot.Statuses;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class StatusTool(
    RuntimeStatus status,
    AgentSession session) : ITool
{
    public string Name => "status";

    public string Description => "Query current runtime, mode, and profile status without adding it to the system prompt.";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = JsonSerializer.Deserialize(invocation.ArgumentsJson, StatusToolJsonContext.Default.StatusToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return $"error: {failure.Message}";
        }

        var text = await status.Observe(session, selection, selection.Profile, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? "No status is currently available." : text;
    }

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input;
}
