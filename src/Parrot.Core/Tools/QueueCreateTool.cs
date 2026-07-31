using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Queues;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class QueueCreateTool(QueueStore queues) : ITool
{
    public string Name => "queue_create";

    public string Description => "Explicitly create a persistent queue shared by agents in the current user session. Queue names may contain lowercase ASCII letters, numbers, and hyphens.";

    public string ParametersJson => Input.Descriptor;

    public Task<string> Execute(string argumentsJson, CancellationToken cancellationToken) => QueueToolExecution.Execute(() =>
    {
        var input = QueueToolExecution.Deserialize(argumentsJson, QueueToolJsonContext.Default.QueueCreateToolInput);
        return QueueToolExecution.Serialize(queues.Create(QueueToolExecution.RequireName(input.Name), input.Description ?? string.Empty));
    });

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [Description("Queue name containing lowercase ASCII letters, numbers, and hyphens.")]
        [JsonPropertyName("name")]
        [ToolPattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
        [ToolRequired]
        public string? Name { get; init; }

        [Description("Human-readable description of the queue.")]
        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }
}
