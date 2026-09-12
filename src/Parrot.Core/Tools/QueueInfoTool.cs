using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueInfoTool(IAgentQueues queues) : ITool
{
    public string Name => "queue_info";

    public Task<ToolExecutionResult> Execute(ToolInvocation invocation, AgentTurnSelection selection, CancellationToken cancellationToken) => QueueToolExecution.Execute(invocation, () =>
    {
        var input = QueueToolExecution.Deserialize(invocation.ArgumentsJson, QueueToolJsonContext.Default.QueueInfoToolInput);
        return QueueToolExecution.Serialize(queues.Get(QueueToolExecution.RequireName(input.Name)));
    });

    internal sealed class Input
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }
}
