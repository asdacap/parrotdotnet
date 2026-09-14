using System.Buffers;
using System.Text.Json;

namespace Parrot.AgentTasks;

internal sealed record AgentTaskGraphResult(AgentTaskExecutionStatus Status, IReadOnlyList<AgentTaskResult> Tasks)
{
    internal string Serialize() => Serialize(Status, Tasks);

    internal static string SerializeNested(IReadOnlyList<AgentTaskResult> tasks)
    {
        var status = tasks.All(task => task.Status == AgentTaskExecutionStatus.Succeeded)
            ? AgentTaskExecutionStatus.Succeeded
            : tasks.Any(task => task.Status == AgentTaskExecutionStatus.Canceled)
                ? AgentTaskExecutionStatus.Canceled
                : AgentTaskExecutionStatus.Failed;
        return Serialize(status, tasks);
    }

    private static string Serialize(
        AgentTaskExecutionStatus status,
        IReadOnlyList<AgentTaskResult> tasks)
    {
        var wire = new AgentTaskResultWire(StatusText(status), [.. tasks.Select(Map)]);
        return JsonSerializer.Serialize(wire, AgentTaskWireJsonContext.Default.AgentTaskResultWire);
    }

    private static AgentTaskResultNodeWire Map(AgentTaskResult result) => new(
        result.Name,
        StatusText(result.Status),
        result.AttemptCount,
        result.Context,
        result.Result,
        MapPatch(result.TaskPatch),
        result.Execution,
        result.Verdict is null ? null : VerdictText(result.Verdict.Kind),
        result.Verdict?.Evidence,
        result.RetryFeedback,
        result.Failure ?? result.Verdict?.Feedback,
        result.BlockedBy,
        result.Tasks?.Select(Map).ToArray());

    private static AgentTaskPatchResultWire? MapPatch(AgentTaskPatch? patch)
    {
        if (patch is null)
        {
            return null;
        }

        return new AgentTaskPatchResultWire(
            patch.Description,
            patch.Payload is null ? null : PayloadElement(patch.Payload),
            patch.AcceptanceCriteria,
            patch.Model.IsSpecified ? patch.Model.Value : null);
    }

    private static JsonElement PayloadElement(AgentTaskPayload payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WritePayload(writer, payload);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WritePayload(Utf8JsonWriter writer, AgentTaskPayload payload)
    {
        if (payload.Instruction is not null)
        {
            writer.WriteStringValue(payload.Instruction);
            return;
        }

        writer.WriteStartArray();
        foreach (var task in payload.Tasks ?? [])
        {
            writer.WriteStartObject();
            writer.WriteString("name", task.Name);
            if (task.Dependencies.Count > 0)
            {
                writer.WriteStartArray("dependencies");
                foreach (var dependency in task.Dependencies)
                {
                    writer.WriteStringValue(dependency);
                }

                writer.WriteEndArray();
            }

            writer.WriteString("description", task.Description);
            writer.WritePropertyName("payload");
            WritePayload(writer, task.Payload);
            writer.WriteString("acceptance_criteria", task.AcceptanceCriteria);
            if (task.Model is not null)
            {
                writer.WriteString("model", task.Model);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string StatusText(AgentTaskExecutionStatus status) => status.ToString().ToLowerInvariant();

    private static string VerdictText(AcceptanceVerdictKind verdict) => verdict switch
    {
        AcceptanceVerdictKind.Accept => "accept",
        AcceptanceVerdictKind.RejectAndRetry => "reject_and_retry",
        AcceptanceVerdictKind.RejectAndHalt => "reject_and_halt",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };
}
