using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Parrot.Agent;

internal sealed record WaitAgentResult(
    string SessionId,
    string Name,
    AgentTaskStatus Status,
    bool Yielded,
    long ElapsedMilliseconds,
    string Output,
    string Error)
{
    public string Format()
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("task_id", SessionId);
            writer.WriteString("name", Name);
            writer.WriteString("kind", "agent");
            writer.WriteString("status", Status.ToString().ToLowerInvariant());

            if (Yielded)
            {
                writer.WriteBoolean("yielded", true);
            }

            writer.WriteNumber("elapsed_ms", ElapsedMilliseconds);

            if (Output.Length > 0)
            {
                writer.WriteString("output", Output);
            }

            if (Error.Length > 0)
            {
                writer.WriteString("error", Error);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
