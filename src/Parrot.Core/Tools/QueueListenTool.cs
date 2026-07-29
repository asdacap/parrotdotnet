using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueListenTool(QueueStore queues) : ITool
{
    public string Name => "queue_listen";

    public string Description => "Enable or disable idle notification delivery from an existing shared user-session queue. Listening remains enabled after each FIFO delivery.";

    public string ParametersJson => """{"type":"object","properties":{"name":{"type":"string","pattern":"^[a-z0-9]+(?:-[a-z0-9]+)*$"},"enabled":{"type":"boolean","default":true}},"required":["name"],"additionalProperties":false}""";

    public Task<string> Execute(string argumentsJson, CancellationToken cancellationToken) => QueueToolExecution.Execute(() =>
    {
        var input = QueueToolExecution.Deserialize(argumentsJson);
        return QueueToolExecution.Serialize(queues.Monitor(QueueToolExecution.RequireName(input), input.Enabled ?? true));
    });
}
