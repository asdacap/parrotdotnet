using System.Text.Json;

namespace Parrot.AgentTasks;

internal static class JsonEnvelope
{
    private const int MinimumFenceLength = 3;

    internal static string Extract(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = TrimByteOrderMark(text);
        return SelectFencedValue(trimmed) ?? SelectTrailingValue(trimmed)
            ?? throw new ArgumentException("The response does not end with a JSON object or array envelope.");
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

    private static string? SelectTrailingValue(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is '{' or '[' && IsEnvelope(text[index..]))
            {
                return text[index..];
            }
        }

        return null;
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

    private static bool IsEnvelope(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string TrimByteOrderMark(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 0 && trimmed[0] == '﻿' ? trimmed[1..].Trim() : trimmed;
    }
}
