using System.Text.Json;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class TodoReadTool(AgentSession session) : ITool
{
    public string Name => "todoread";

    public Task<ToolExecutionResult> Execute(ToolInvocation invocation, AgentTurnSelection selection, CancellationToken cancellationToken)
    {
        try
        {
            _ = JsonSerializer.Deserialize(invocation.ArgumentsJson, TodoJsonContext.Default.TodoReadInput)
                ?? throw new FormatException("Tool arguments must be an object.");

            var items = session.Todos.Read(cancellationToken).Select(TodoTools.ToWire).ToArray();
            return Task.FromResult<ToolExecutionResult>(JsonSerializer.Serialize(items, TodoJsonContext.Default.TodoWireItemArray));
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return Task.FromResult<ToolExecutionResult>($"error: {failure.Message}");
        }
    }

    internal sealed class Input;
}
