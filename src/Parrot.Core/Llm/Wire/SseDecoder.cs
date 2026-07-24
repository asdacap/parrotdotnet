using System.Runtime.CompilerServices;
using System.Text;

namespace Parrot.Llm.Wire;

// Incrementally decodes an SSE byte stream into records. A record ends at a
// blank line; `data:` fields accumulate and join with newlines. The port of
// Go's protocol/sse: field lines are event/data/id, a leading space after the
// colon is stripped, comment lines (starting ':') are ignored, and a record
// larger than maxEventBytes is a hard error.
internal static class SseDecoder
{
    public const int DefaultMaxEventBytes = 4 << 20;

    public static async IAsyncEnumerable<SseEvent> Decode(
        Stream stream,
        int maxEventBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (maxEventBytes <= 0)
        {
            maxEventBytes = DefaultMaxEventBytes;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        var eventName = string.Empty;
        var id = string.Empty;
        var data = new List<string>();
        var size = 0;
        var hasFields = false;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            size += line.Length + 1;

            if (size > maxEventBytes)
            {
                throw new WireProtocolException($"sse: event exceeds maximum size {maxEventBytes}");
            }

            if (line.Length == 0)
            {
                if (hasFields)
                {
                    yield return new SseEvent(eventName, string.Join('\n', data), id);
                    eventName = string.Empty;
                    id = string.Empty;
                    data.Clear();
                    hasFields = false;
                }

                size = 0;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            var name = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..];

            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (name)
            {
                case "event":
                    eventName = value;
                    hasFields = true;
                    break;

                case "data":
                    data.Add(value);
                    hasFields = true;
                    break;

                case "id":
                    if (!value.Contains('\0', StringComparison.Ordinal))
                    {
                        id = value;
                    }

                    hasFields = true;
                    break;

                default:
                    break;
            }
        }

        if (hasFields)
        {
            yield return new SseEvent(eventName, string.Join('\n', data), id);
        }
    }
}
