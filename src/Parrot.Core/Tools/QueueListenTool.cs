using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Queues;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class QueueListenTool(QueueStore queues) : ITool
{
    public string Name => "queue_listen";

    public string Description => "Enable or disable idle notification delivery from an existing shared user-session queue. Listening remains enabled after each FIFO delivery.";

    public string ParametersJson => Input.Descriptor;

    public Task<ToolExecutionResult> Execute(ToolInvocation invocation, CancellationToken cancellationToken) => QueueToolExecution.Execute(() =>
    {
        var input = QueueToolExecution.Deserialize(invocation.ArgumentsJson, QueueToolJsonContext.Default.QueueListenToolInput);
        return QueueToolExecution.Serialize(queues.Monitor(QueueToolExecution.RequireName(input.Name), input.Enabled ?? true));
    });

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [Description("Name of the queue whose idle notifications to configure.")]
        [JsonPropertyName("name")]
        [ToolPattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
        [ToolRequired]
        public string? Name { get; init; }

        [Description("Whether idle notification delivery is enabled.")]
        [JsonPropertyName("enabled")]
        [ToolDefaultBool(true)]
        public bool? Enabled { get; init; }
    }
}
