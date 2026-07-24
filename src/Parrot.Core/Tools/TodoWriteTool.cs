using System.Text.Json;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class TodoWriteTool(AgentSession session) : ITool
{
    public string Name => "todowrite";

    public string Description => "Transactionally replace the current session's ordered todo list.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"todos":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"content":{"type":"string","minLength":1},"status":{"type":"string","enum":["pending","in_progress","completed","cancelled"]},"priority":{"type":"string","enum":["high","medium","low"]}},"required":["content","status","priority"],"additionalProperties":false}}},"required":["todos"],"additionalProperties":false}
        """;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(argumentsJson, TodoJsonContext.Default.TodoWriteInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var wireItems = input.Todos ?? throw new FormatException("Tool arguments require an array 'todos'.");
            var items = wireItems.Select(TodoTools.ToDomain).ToArray();
            var replaced = await session.Todos.Replace(items, cancellationToken).ConfigureAwait(false);
            var normalized = replaced.Select(TodoTools.ToWire).ToArray();
            return JsonSerializer.Serialize(normalized, TodoJsonContext.Default.TodoWireItemArray);
        }
        catch (Exception failure) when (failure is JsonException or FormatException or ArgumentException)
        {
            return $"error: {failure.Message}";
        }
    }
}
