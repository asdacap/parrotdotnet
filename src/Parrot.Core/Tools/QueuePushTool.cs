using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Queues;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class QueuePushTool(QueueStore queues, UserSession owner) : ITool
{
    public string Name => "queue_push";

    public string Description => "Push strings onto an existing shared user-session queue. Direction defaults to back.";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        try
        {
            var input = QueueToolExecution.Deserialize(invocation.ArgumentsJson, QueueToolJsonContext.Default.QueuePushToolInput);
            var items = input.Items ?? throw new FormatException("Tool arguments require an array 'items'.");
            var info = queues.Push(QueueToolExecution.RequireName(input.Name), items, QueueToolExecution.ParseDirection(input.Direction));
            await owner.NotifyQueuePush(cancellationToken).ConfigureAwait(false);
            return QueueToolExecution.Serialize(info);
        }
        catch (Exception failure) when (failure is JsonException or FormatException or QueueException)
        {
            return $"error: {failure.Message}";
        }
    }

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [Description("Name of the queue to receive the items.")]
        [JsonPropertyName("name")]
        [ToolPattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
        [ToolRequired]
        public string? Name { get; init; }

        [Description("Strings to push onto the queue.")]
        [JsonPropertyName("items")]
        [ToolRequired]
        public string[]? Items { get; init; }

        [Description("End of the queue onto which the items are pushed.")]
        [JsonPropertyName("direction")]
        [ToolDefaultString("back")]
        [ToolStringEnum("front", "back")]
        public string? Direction { get; init; }
    }
}
