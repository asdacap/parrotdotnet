using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Queues;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class QueueListenTool(AgentQueues queues) : ITool
{
    public string Name => "queue_listen";

    public string ParametersJson => Input.Descriptor;

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
            return $"error: {failure.Message}";
        }
    }

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [JsonPropertyName("name")]
        [ToolPattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
        [ToolRequired]
        public string? Name { get; init; }

        [JsonPropertyName("enabled")]
        [ToolDefaultBool(true)]
        public bool? Enabled { get; init; }
    }
}
