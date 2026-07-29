using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueCreateTool(QueueStore queues) : ITool
{
    public string Name => "queue_create";

    public string Description => "Explicitly create a persistent queue shared by agents in the current user session. Queue names may contain lowercase ASCII letters, numbers, and hyphens.";

    public string ParametersJson => """{"type":"object","properties":{"name":{"type":"string","pattern":"^[a-z0-9]+(?:-[a-z0-9]+)*$"},"description":{"type":"string"}},"required":["name"],"additionalProperties":false}""";

    public Task<string> Execute(string argumentsJson, CancellationToken cancellationToken) => QueueToolExecution.Execute(() =>
    {
        var input = QueueToolExecution.Deserialize(argumentsJson);
        return QueueToolExecution.Serialize(queues.Create(QueueToolExecution.RequireName(input), input.Description ?? string.Empty));
    });
}
