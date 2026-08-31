using System.Text.Json;
using Parrot.Agent;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class TodoReadTool(AgentSession session) : ITool
{
    public string Name => "todoread";

    public string ParametersJson => Input.Descriptor;

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

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input;
}
