using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.AgentTasks;

namespace Parrot.Tools;

internal sealed class GetAgentTasksTool(IAgentTaskService agentTasks) : ITool
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Name => "get_agent_tasks";

    public Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        Input input;
        try
        {
            input = JsonSerializer.Deserialize(
                invocation.ArgumentsJson,
                AgentTasksToolJsonContext.Default.GetAgentTasksToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Error(invocation, failure.Message));
        }

        if (input.Name is not { } name)
        {
            return Task.FromResult<ToolExecutionResult>(Write(writer => WriteSummaries(writer, agentTasks.Snapshot())));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Error(invocation, "Tool arguments require a nonblank string 'name'."));
        }

        return Task.FromResult<ToolExecutionResult>(
            agentTasks.CaptureDetail(name) is { } found
                ? Write(writer => WriteTask(writer, found.Task, found.AgentName, true))
                : ToolResultFormatter.Error(invocation, $"AgentTask '{name}' does not exist."));
    }

    private static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteSummaries(Utf8JsonWriter writer, IReadOnlyList<AgentTask> tasks)
    {
        writer.WriteStartArray();
        foreach (var task in tasks)
        {
            writer.WriteStartObject();
            writer.WriteString("name", task.Name);
            writer.WriteString("state", State(task.State));
            writer.WriteString("description", task.Description);
            writer.WriteBoolean("hidden", task.Hidden);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteTask(Utf8JsonWriter writer, AgentTask task, string? agentName, bool withExecution)
    {
        writer.WriteStartObject();
        writer.WriteString("name", task.Name);
        writer.WriteStartArray("dependencies");
        foreach (var dependency in task.Dependencies)
        {
            writer.WriteStringValue(dependency);
        }

        writer.WriteEndArray();
        writer.WriteString("description", task.Description);
        if (task.Payload.Instruction is { } instruction)
        {
            writer.WriteString("payload", instruction);
        }
        else
        {
            writer.WriteStartArray("payload");
            foreach (var child in task.Payload.Tasks ?? [])
            {
                WriteTask(writer, child, null, false);
            }

            writer.WriteEndArray();
        }

        writer.WriteString("acceptance_criteria", task.AcceptanceCriteria);
        if (task.Model is { } model)
        {
            writer.WriteString("model", model);
        }

        writer.WriteBoolean("hidden", task.Hidden);
        if (agentName is not null)
        {
            writer.WriteString("agent_name", agentName);
        }

        if (withExecution)
        {
            writer.WriteString("state", State(task.State));
            if (task.Result is { } result)
            {
                writer.WriteString("result", result);
            }

            if (task.Failure is { } failure)
            {
                writer.WriteString("failure", failure);
            }
        }

        writer.WriteEndObject();
    }

    private static string State(AgentTaskExecutionStatus state) => state.ToString().ToLowerInvariant();

    internal sealed class Input
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }
}
