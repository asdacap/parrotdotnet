using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueCreateTool(QueueStore queues) : ITool
{
    public string Name => "queue_create";

    public string Description => "Explicitly create a persistent queue shared by agents in the current user session. Queue names may contain lowercase ASCII letters, numbers, and hyphens.";

    public string ParametersJson => QueueCreateToolInput.Descriptor;

    public Task<string> Execute(string argumentsJson, CancellationToken cancellationToken) => QueueToolExecution.Execute(() =>
    {
        var input = QueueToolExecution.Deserialize(argumentsJson, QueueToolJsonContext.Default.QueueCreateToolInput);
        return QueueToolExecution.Serialize(queues.Create(QueueToolExecution.RequireName(input.Name), input.Description ?? string.Empty));
    });
}
