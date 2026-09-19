using System.Text.Json;

namespace Parrot.AgentTasks;

internal static class JsonEnvelope
{
    private const int MinimumFenceLength = 3;

    internal static string Extract(string text, params string[] markerProperties)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = TrimByteOrderMark(text);
        if (IsEnvelope(trimmed))
        {
            return trimmed;
        }

        return SelectFencedValue(trimmed) ?? SelectBalancedValue(trimmed, markerProperties);
    }

    private static string? SelectFencedValue(string text)
    {
        var blocks = CollectFencedBlocks(text);
        for (var index = blocks.Count - 1; index >= 0; index--)
        {
            var candidate = TrimByteOrderMark(text[blocks[index]]);
            if (IsEnvelope(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string SelectBalancedValue(string text, string[] markerProperties)
    {
        Range? marker = null;
        Range? objectValue = null;
        Range? arrayValue = null;
        var start = -1;
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"' when depth > 0:
                    inString = true;
                    break;
                case '{' or '[':
                    if (depth == 0)
                    {
                        start = index;
                    }

                    depth++;
                    break;
                case '}' or ']' when depth > 0:
                    depth--;
                    if (depth == 0)
                    {
                        RecordCandidate(new Range(start, index + 1));
                    }

                    break;
            }
        }

        return text[marker ?? objectValue ?? arrayValue
            ?? throw new ArgumentException("The response does not contain a JSON object or array envelope.")];

        void RecordCandidate(Range range)
        {
            using var document = ParseEnvelopeOrNull(text[range]);
            if (document is null)
            {
                return;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                objectValue = range;
                if (HasMarker(document.RootElement, markerProperties))
                {
                    marker = range;
                }
            }
            else
            {
                arrayValue = range;
            }
        }
    }

    private static List<Range> CollectFencedBlocks(string text)
    {
        var blocks = new List<Range>();
        var lineStart = 0;
        var bodyStart = -1;

        while (lineStart < text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            if (StartsFence(text, lineStart, lineEnd))
            {
                if (bodyStart < 0)
                {
                    bodyStart = lineEnd < text.Length ? lineEnd + 1 : lineEnd;
                }
                else
                {
                    blocks.Add(new Range(bodyStart, lineStart));
                    bodyStart = -1;
                }
            }

            if (lineEnd >= text.Length)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }

        if (bodyStart >= 0 && bodyStart < text.Length)
        {
            blocks.Add(new Range(bodyStart, text.Length));
        }

        return blocks;
    }

    private static bool StartsFence(string text, int lineStart, int lineEnd)
    {
        var index = lineStart;
        while (index < lineEnd && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        var backticks = 0;
        while (index < lineEnd && text[index] == '`')
        {
            backticks++;
            index++;
        }

        return backticks >= MinimumFenceLength;
    }

    private static bool HasMarker(JsonElement root, string[] markerProperties)
    {
        foreach (var property in root.EnumerateObject())
        {
            foreach (var marker in markerProperties)
            {
                if (string.Equals(property.Name, marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsEnvelope(string text)
    {
        using var document = ParseEnvelopeOrNull(text);
        return document is not null;
    }

    private static JsonDocument? ParseEnvelopeOrNull(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return document;
        }

        document.Dispose();
        return null;
    }

    private static string TrimByteOrderMark(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 0 && trimmed[0] == '\uFEFF' ? trimmed[1..].Trim() : trimmed;
    }
}
