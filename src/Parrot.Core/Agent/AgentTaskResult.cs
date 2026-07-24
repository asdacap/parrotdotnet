using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Parrot.Agent;

internal sealed record AgentTaskResult(
    string SessionId,
    string Name,
    int Depth,
    AgentTaskStatus Status,
    bool Yielded,
    long ElapsedMilliseconds,
    string Output,
    string Error)
{
    public string FormatSpawn() => Format(spawn: true);

    public string FormatWait() => Format(spawn: false);

    private string Format(bool spawn)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(spawn ? "session_id" : "task_id", SessionId);
            writer.WriteString("name", Name);
            writer.WriteString("kind", "agent");
            writer.WriteString("status", Status.ToString().ToLowerInvariant());

            if (spawn)
            {
                writer.WriteNumber("depth", Depth);
            }
            else
            {
                if (Yielded)
                {
                    writer.WriteBoolean("yielded", true);
                }

                writer.WriteNumber("elapsed_ms", ElapsedMilliseconds);
            }

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
