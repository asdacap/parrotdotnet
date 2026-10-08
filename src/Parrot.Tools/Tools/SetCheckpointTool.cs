using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class SetCheckpointTool(ICheckpointService checkpoints) : ITool
{
    public string Name => "set_checkpoint";

    public Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = ToolInputConversion.Deserialize(invocation.ArgumentsJson, AgentProcessToolJsonContext.Default.SetCheckpointToolInput);
            var title = input.Title ?? throw new FormatException("Tool arguments require a string 'title'.");
            checkpoints.SetCheckpoint(title, invocation.AssistantSequence, invocation.CallId);
            return Task.FromResult<ToolExecutionResult>(title);
        }
        catch (Exception failure) when (failure is JsonException or FormatException or ArgumentException or Store.InputConflictException)
        {
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Error(invocation, failure.Message));
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("title")]
        public string? Title { get; init; }
    }
}
