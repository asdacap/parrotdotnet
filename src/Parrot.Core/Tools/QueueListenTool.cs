using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueListenTool(AgentQueues queues) : ITool
{
    public string Name => "queue_listen";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = QueueToolExecution.Deserialize(invocation.ArgumentsJson, QueueToolJsonContext.Default.QueueListenToolInput);
            var info = await queues.Listen(
                QueueToolExecution.RequireName(input.Name),
                input.Enabled ?? true,
                cancellationToken).ConfigureAwait(false);
            return QueueToolExecution.Serialize(info);
        }
        catch (Exception failure) when (failure is System.Text.Json.JsonException or FormatException or QueueException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("enabled")]
        public bool? Enabled { get; init; }
    }
}
