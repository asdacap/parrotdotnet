using System.Text.Json;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class TodoReadTool(AgentSession session) : ITool
{
    public string Name => "todoread";

    public string Description => "Read the current session's ordered todo list.";

    public string ParametersJson => TodoReadInput.Descriptor;

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
}
