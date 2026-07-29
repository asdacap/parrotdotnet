using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueInfoTool(QueueStore queues) : ITool
{
    public string Name => "queue_info";

    public string Description => "Get metadata and the current size of an existing shared user-session queue.";

    public string ParametersJson => """{"type":"object","properties":{"name":{"type":"string","pattern":"^[a-z0-9]+(?:-[a-z0-9]+)*$"}},"required":["name"],"additionalProperties":false}""";

    public Task<string> Execute(string argumentsJson, CancellationToken cancellationToken) => QueueToolExecution.Execute(() => QueueToolExecution.Serialize(queues.Get(QueueToolExecution.RequireName(QueueToolExecution.Deserialize(argumentsJson)))));
}
