using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class TodoWriteTool(AgentSession session) : ITool
{
    public string Name => "todowrite";

    public string Description => "Transactionally replace the current session's ordered todo list.";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(ToolInvocation invocation, CancellationToken cancellationToken)
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

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [Description("Complete ordered todo list that replaces the current list.")]
        [JsonPropertyName("todos")]
        [ToolRequired]
        public Item[]? Todos { get; init; }

        [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
        internal sealed partial class Item
        {
            [Description("Stable todo identifier. Omit it when adding a new todo.")]
            [JsonPropertyName("id")]
            public string? Id { get; init; }

            [Description("Todo text.")]
            [JsonPropertyName("content")]
            [ToolMinLength(1)]
            [ToolRequired]
            public string? Content { get; init; }

            [Description("Current todo state.")]
            [JsonPropertyName("status")]
            [ToolStringEnum("pending", "in_progress", "completed", "cancelled")]
            [ToolRequired]
            public string? Status { get; init; }

            [Description("Todo urgency.")]
            [JsonPropertyName("priority")]
            [ToolStringEnum("high", "medium", "low")]
            [ToolRequired]
            public string? Priority { get; init; }
        }
    }
}
