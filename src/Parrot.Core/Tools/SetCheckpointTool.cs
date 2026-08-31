using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class SetCheckpointTool(AgentSession session) : ITool
{
    public string Name => "set_checkpoint";

    public string ParametersJson => Input.Descriptor;

    public Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, AgentProcessToolJsonContext.Default.SetCheckpointToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var title = input.Title ?? throw new FormatException("Tool arguments require a string 'title'.");
            session.SetCheckpoint(title, invocation.AssistantSequence, invocation.CallId);
            return Task.FromResult<ToolExecutionResult>(title);
        }
        catch (Exception failure) when (failure is JsonException or FormatException or ArgumentException or Store.InputConflictException)
        {
            return Task.FromResult<ToolExecutionResult>($"error: {failure.Message}");
        }
    }

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [JsonPropertyName("title")]
        [ToolMinLength(1)]
        [ToolRequired]
        public string? Title { get; init; }
    }
}
