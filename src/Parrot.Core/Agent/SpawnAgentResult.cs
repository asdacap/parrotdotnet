using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Parrot.Agent;

internal sealed record SpawnAgentResult(string SessionId, string Name, int Depth)
{
    public string Format()
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("session_id", SessionId);
            writer.WriteString("name", Name);
            writer.WriteString("kind", "agent");
            writer.WriteString("status", "running");
            writer.WriteNumber("depth", Depth);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
