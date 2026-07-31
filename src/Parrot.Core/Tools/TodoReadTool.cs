using System.Text.Json;
using Parrot.Agent;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class TodoReadTool(AgentSession session) : ITool
{
    public string Name => "todoread";

    public string Description => "Read the current session's ordered todo list.";

    public string ParametersJson => Input.Descriptor;

    public Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        try
        {
            _ = JsonSerializer.Deserialize(argumentsJson, TodoJsonContext.Default.TodoReadInput)
                ?? throw new FormatException("Tool arguments must be an object.");

            var items = session.Todos.Read(cancellationToken).Select(TodoTools.ToWire).ToArray();
            return Task.FromResult(JsonSerializer.Serialize(items, TodoJsonContext.Default.TodoWireItemArray));
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return Task.FromResult($"error: {failure.Message}");
        }
    }

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input;
}
