using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Parrot.AgentTasks;

internal static class AgentTaskPromptFormatter
{
    internal static string Format(EffectiveAgentTask task)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
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
            writer.WritePropertyName("payload");
            WritePayload(writer, task.Payload);
            writer.WriteString("acceptance_criteria", task.AcceptanceCriteria);
            if (task.Model is not null)
            {
                writer.WriteString("model", task.Model);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
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
            writer.WriteStartArray("dependencies");
            foreach (var dependency in task.Dependencies)
            {
                writer.WriteStringValue(dependency);
            }

            writer.WriteEndArray();
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
}
