using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Queues;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class QueueCloseTool(AgentQueues queues) : ITool
{
    public string Name => "queue_close";

    public string Description => "Close an accessible queue owned by the invoking agent or its direct parent. Closing is idempotent, rejects later pushes, preserves queued items for draining, and allows polling queue_take calls to finish promptly without waking queue listeners.";

    public string ParametersJson => Input.Descriptor;

    public Task<ToolExecutionResult> Execute(ToolInvocation invocation, AgentTurnSelection selection, CancellationToken cancellationToken) => QueueToolExecution.Execute(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = QueueToolExecution.Deserialize(invocation.ArgumentsJson, QueueToolJsonContext.Default.QueueCloseToolInput);
        return QueueToolExecution.Serialize(queues.Close(QueueToolExecution.RequireName(input.Name)));
    });

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [Description("Name of the queue to close.")]
        [JsonPropertyName("name")]
        [ToolPattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
        [ToolRequired]
        public string? Name { get; init; }
    }
}
