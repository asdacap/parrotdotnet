using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueCreateTool(IAgentQueues queues) : ITool
{
    public string Name => "queue_create";

    public Task<ToolExecutionResult> Execute(ToolInvocation invocation, AgentTurnSelection selection, CancellationToken cancellationToken) => QueueToolExecution.Execute(invocation, () =>
    {
        var input = QueueToolExecution.Deserialize(invocation.ArgumentsJson, QueueToolJsonContext.Default.QueueCreateToolInput);
        return QueueToolExecution.Serialize(queues.Create(QueueToolExecution.RequireName(input.Name), input.Description ?? string.Empty));
    });

    internal sealed class Input
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }
}
