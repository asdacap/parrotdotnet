using System.Text.Json;
using Parrot.Agent;
using Parrot.Statuses;

namespace Parrot.Tools;

internal sealed class StatusTool(
    RuntimeStatus status,
    AgentSession session,
    AgentTurnSelection selection) : ITool
{
    public string Name => "status";

    public string Description => "Query current runtime, mode, and profile status without adding it to the system prompt.";

    public string ParametersJson => """{"type":"object","additionalProperties":false}""";

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "error: Tool arguments must be an object.";
            }

            if (document.RootElement.EnumerateObject().Any())
            {
                return "error: Tool arguments contain an unexpected property.";
            }
        }
        catch (JsonException failure)
        {
            return $"error: {failure.Message}";
        }

        if (selection.Profile is null)
        {
            return "No status is currently available.";
        }

        var text = await status.Observe(session, selection, selection.Profile, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? "No status is currently available." : text;
    }
}
