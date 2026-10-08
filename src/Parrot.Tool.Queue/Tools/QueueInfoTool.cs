using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueInfoTool(IAgentQueues queues, IAgentResolver resolver) : ITool
{
    public string Name => "queue_info";

    public Task<ToolExecutionResult> Execute(ToolInvocation invocation, AgentTurnSelection selection, CancellationToken cancellationToken) => QueueToolExecution.Execute(invocation, () =>
    {
        var input = ToolInputConversion.Deserialize(invocation.ArgumentsJson, QueueToolJsonContext.Default.QueueInfoToolInput);
        var name = QueueToolExecution.RequireName(input.Name);
        if (name.Contains('/', StringComparison.Ordinal))
        {
            var resource = resolver.ResolveResource(name);
            return QueueToolExecution.Serialize(resource.Scope.GetService<IAgentQueues>().Local.Get(resource.Name));
        }

        return QueueToolExecution.Serialize(queues.Get(name));
    });

    internal sealed class Input
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }
}
