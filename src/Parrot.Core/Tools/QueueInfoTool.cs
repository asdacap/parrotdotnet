using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Queues;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class QueueInfoTool(AgentQueues queues) : ITool
{
    public string Name => "queue_info";

    public string Description => "Get metadata and the current size of an accessible queue owned by the invoking agent or its direct parent.";

    public string ParametersJson => Input.Descriptor;

    public Task<ToolExecutionResult> Execute(ToolInvocation invocation, CancellationToken cancellationToken) => QueueToolExecution.Execute(() =>
    {
        var input = QueueToolExecution.Deserialize(invocation.ArgumentsJson, QueueToolJsonContext.Default.QueueInfoToolInput);
        return QueueToolExecution.Serialize(queues.Get(QueueToolExecution.RequireName(input.Name)));
    });

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [Description("Name of the queue whose metadata and size to retrieve.")]
        [JsonPropertyName("name")]
        [ToolPattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
        [ToolRequired]
        public string? Name { get; init; }
    }
}
