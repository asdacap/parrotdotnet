using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Parrot.Agent;

internal sealed record AgentSendResult(string Name, string MessageId, bool FollowUp)
{
    public string Format()
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", Name);

            if (FollowUp)
            {
                writer.WriteString("status", "running");
            }
            else
            {
                writer.WriteString("message_id", MessageId);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
