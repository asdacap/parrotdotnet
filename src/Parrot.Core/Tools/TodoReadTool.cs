using System.Text.Json;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class TodoReadTool(AgentSession session) : ITool
{
    public string Name => "todoread";

    public string Description => "Read the current session's ordered todo list.";

    public string ParametersJson => """{"type":"object","additionalProperties":false}""";

    public Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult("error: Tool arguments must be an object.");
            }

            if (document.RootElement.EnumerateObject().Any())
            {
                return Task.FromResult("error: Tool arguments contain an unexpected property.");
            }

            var items = session.Todos.Read(cancellationToken).Select(TodoTools.ToWire).ToArray();
            return Task.FromResult(JsonSerializer.Serialize(items, TodoJsonContext.Default.TodoWireItemArray));
        }
        catch (JsonException failure)
        {
            return Task.FromResult($"error: {failure.Message}");
        }
    }
}
