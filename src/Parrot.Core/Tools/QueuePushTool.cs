using System.Text.Json;
using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueuePushTool(QueueStore queues, UserSession owner) : ITool
{
    public string Name => "queue_push";

    public string Description => "Push strings onto an existing shared user-session queue. Direction defaults to back.";

    public string ParametersJson => QueuePushToolInput.Descriptor;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        try
        {
            var input = QueueToolExecution.Deserialize(argumentsJson, QueueToolJsonContext.Default.QueuePushToolInput);
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
}
