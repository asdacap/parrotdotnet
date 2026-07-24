using System.Net;
using System.Text;

namespace Parrot.Web;

internal static class HtmlText
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "br", "div", "dl", "fieldset", "figcaption", "figure",
        "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol",
        "p", "pre", "section", "table", "tr", "ul",
    };

    private static readonly HashSet<string> HiddenTags = new(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "template",
    };

    public static string Extract(string input)
    {
        var output = new StringBuilder();
        var hidden = string.Empty;
        var position = 0;

        while (position < input.Length)
        {
            if (input[position] != '<')
            {
                var next = input.IndexOf('<', position);
                next = next < 0 ? input.Length : next;

                if (hidden.Length == 0)
                {
                    _ = output.Append(input, position, next - position);
                }

                position = next;
                continue;
            }

            if (input.AsSpan(position).StartsWith("<!--", StringComparison.Ordinal))
            {
                var endComment = input.IndexOf("-->", position + 4, StringComparison.Ordinal);

                if (endComment < 0)
                {
                    break;
                }

                position = endComment + 3;
                continue;
            }

            var end = FindTagEnd(input, position + 1);

            if (end < 0)
            {
                if (hidden.Length == 0)
                {
                    _ = output.Append(input, position, input.Length - position);
                }

                break;
            }

            var (tag, closing) = ReadTag(input.AsSpan(position + 1, end - position - 1));

            if (HiddenTags.Contains(tag))
            {
                if (closing && hidden == tag)
                {
                    hidden = string.Empty;
                }
                else if (!closing && hidden.Length == 0)
                {
                    hidden = tag;
                }
            }

            if (hidden.Length == 0 && BlockTags.Contains(tag))
            {
                _ = output.Append('\n');
            }

            position = end + 1;
        }

        return Normalize(WebFetchText.StripControls(WebUtility.HtmlDecode(output.ToString())));
    }

    private static int FindTagEnd(string input, int start)
    {
        var quote = '\0';

        for (var index = start; index < input.Length; index++)
        {
            if (quote != '\0')
            {
                if (input[index] == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (input[index] is '\'' or '"')
            {
                quote = input[index];
            }
            else if (input[index] == '>')
            {
                return index;
            }
        }

        return -1;
    }

    private static (string Tag, bool Closing) ReadTag(ReadOnlySpan<char> raw)
    {
        raw = raw.Trim();

        if (raw.IsEmpty || raw[0] is '!' or '?')
        {
            return (string.Empty, false);
        }

        var closing = raw[0] == '/';
        raw = closing ? raw[1..].TrimStart() : raw;
        var length = 0;

        while (length < raw.Length && char.IsLetterOrDigit(raw[length]))
        {
            length++;
        }

        return (raw[..length].ToString().ToLowerInvariant(), closing);
    }

    private static string Normalize(string text)
    {
        var result = new List<string>();
        var blank = true;

        foreach (var rawLine in text.Replace('\r', '\n').Split('\n'))
        {
            var line = string.Join(' ', rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            if (line.Length == 0)
            {
                if (!blank)
                {
                    result.Add(string.Empty);
                    blank = true;
                }

                continue;
            }

            result.Add(line);
            blank = false;
        }

        if (result is [.., ""])
        {
            result.RemoveAt(result.Count - 1);
        }

        return string.Join('\n', result);
    }
}
