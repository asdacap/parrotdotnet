using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class TodoWriteTool(AgentSession session) : ITool
{
    public string Name => "todowrite";

    public async Task<ToolExecutionResult> Execute(ToolInvocation invocation, AgentTurnSelection selection, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, TodoJsonContext.Default.TodoWriteInput)
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

    internal sealed class Input
    {
        [JsonPropertyName("todos")]
        public Item[]? Todos { get; init; }

        internal sealed class Item
        {
            [JsonPropertyName("id")]
            public string? Id { get; init; }

            [JsonPropertyName("content")]
            public string? Content { get; init; }

            [JsonPropertyName("status")]
            public string? Status { get; init; }

            [JsonPropertyName("priority")]
            public string? Priority { get; init; }
        }
    }
}
