using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueInfoTool(QueueStore queues) : ITool
{
    public string Name => "queue_info";

    public string Description => "Get metadata and the current size of an existing shared user-session queue.";

    public string ParametersJson => QueueInfoToolInput.Descriptor;

    public Task<string> Execute(string argumentsJson, CancellationToken cancellationToken) => QueueToolExecution.Execute(() =>
    {
        var input = QueueToolExecution.Deserialize(argumentsJson, QueueToolJsonContext.Default.QueueInfoToolInput);
        return QueueToolExecution.Serialize(queues.Get(QueueToolExecution.RequireName(input.Name)));
    });
}
