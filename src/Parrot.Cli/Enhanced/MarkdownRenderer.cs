using System.Globalization;
using System.Text;

namespace Parrot.Cli.Enhanced;

internal static class MarkdownRenderer
{
    private const int MaxHighlightBytes = 512 * 1024;
    private const int MaxHighlightLines = 10_000;
    private const string AssistantColor = "38;5;195";

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "and", "as", "async", "await", "base", "bool", "break", "case", "catch", "char",
        "class", "const", "continue", "def", "defer", "do", "else", "enum", "except", "export", "extends",
        "false", "finally", "float", "for", "foreach", "from", "func", "function", "go", "if", "implements",
        "import", "in", "int", "interface", "internal", "is", "let", "map", "match", "namespace", "new",
        "nil", "not", "null", "object", "of", "or", "out", "override", "package", "pass", "private",
        "protected", "public", "readonly", "record", "return", "sealed", "static", "string", "struct",
        "super", "switch", "this", "throw", "throws", "true", "try", "type", "typeof", "undefined", "using",
        "var", "virtual", "void", "when", "where", "while", "with", "yield",
    };

    public static IReadOnlyList<string> Render(string prefix, string source, int columns, bool color)
    {
        prefix = TerminalText.Sanitize(prefix);
        source = TerminalText.Sanitize(source).TrimEnd('\r', '\n');
        var lines = source.Split('\n');
        var output = new List<string>();
        var continuation = new string(' ', TerminalText.Width(prefix));
        var first = true;
        var fence = (Marker: '\0', Length: 0, Language: string.Empty);
        var code = new List<string>();

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (fence.Marker != '\0')
            {
                if (IsFenceClose(line, fence.Marker, fence.Length))
                {
                    AddCode(output, first ? prefix : continuation, continuation, code, fence.Language, columns, color);
                    first = false;
                    fence = ('\0', 0, string.Empty);
                    code.Clear();
                }
                else
                {
                    code.Add(line);
                }

                continue;
            }

            if (TryFence(line, out fence))
            {
                continue;
            }

            if (TryTable(lines, index, out var table, out var consumed))
            {
                AddTable(output, first ? prefix : continuation, continuation, table, columns, color);
                first = false;
                index += consumed;
                continue;
            }

            var linePrefix = first ? prefix : continuation;
            AddRuns(
                output,
                Prefix(linePrefix, LineRuns(line, columns - TerminalText.Width(linePrefix))),
                continuation,
                columns,
                color);
            first = false;
        }

        if (fence.Marker != '\0')
        {
            AddCode(output, first ? prefix : continuation, continuation, code, fence.Language, columns, color);
            first = false;
        }

        if (first)
        {
            AddRuns(output, [new Run(prefix, default)], continuation, columns, color);
        }

        return output;
    }

    private static List<Run> LineRuns(string line, int available)
    {
        var leadingLength = line.Length - line.TrimStart(' ').Length;
        var leading = line[..leadingLength];
        var content = line[leadingLength..];
        if (Heading(content, out var heading))
        {
            return Prefix(leading, Inline(heading, new Style { Color = "36", Bold = true }));
        }

        if (content.StartsWith('>'))
        {
            var quote = content[1..].TrimStart(' ');
            return Join([new Run(leading + "│ ", new Style { Color = "90", Dim = true })], Inline(quote, default));
        }

        if (ListMarker(content, out var marker, out var rest))
        {
            return Join(
                [new Run(leading, default), new Run(marker, new Style { Color = "36", Bold = true })],
                Inline(rest, default));
        }

        if (ThematicBreak(content))
        {
            return [new Run(new string('─', Math.Max(3, available)), new Style { Color = "90", Dim = true })];
        }

        return Prefix(leading, Inline(content, default));
    }

    private static List<Run> Inline(string value, Style basis)
    {
        var runs = new List<Run>();
        for (var position = 0; position < value.Length;)
        {
            if (value[position] == '\\' && position + 1 < value.Length && IsEscapable(value[position + 1]))
            {
                Add(runs, value.Substring(position + 1, 1), basis);
                position += 2;
                continue;
            }

            if (TryDelimited(value, position, "**", out var strongEnd)
                || TryDelimited(value, position, "__", out strongEnd))
            {
                Add(runs, value[(position + 2)..strongEnd], basis.Merge(new Style { Bold = true }));
                position = strongEnd + 2;
                continue;
            }

            if (TryDelimited(value, position, "~~", out var strikeEnd))
            {
                Add(runs, value[(position + 2)..strikeEnd], basis.Merge(new Style { Strike = true }));
                position = strikeEnd + 2;
                continue;
            }

            if (value[position] == '`' && TryCode(value, position, out var code, out var codeEnd))
            {
                Add(runs, code, basis.Merge(new Style { Color = "33" }));
                position = codeEnd;
                continue;
            }

            if (value[position] == '[' && TryLink(value, position, out var label, out var url, out var linkEnd))
            {
                Add(runs, label, basis.Merge(new Style { Underline = true }));
                Add(runs, " (" + url + ")", basis.Merge(new Style { Color = "36", Underline = true }));
                position = linkEnd;
                continue;
            }

            if ((value[position] == '*' || value[position] == '_')
                && TryDelimited(value, position, value.Substring(position, 1), out var emphasisEnd))
            {
                Add(runs, value[(position + 1)..emphasisEnd], basis.Merge(new Style { Italic = true }));
                position = emphasisEnd + 1;
                continue;
            }

            var rune = Rune.GetRuneAt(value, position);
            Add(runs, rune.ToString(), basis);
            position += rune.Utf16SequenceLength;
        }

        return runs;
    }

    private static void AddCode(
        List<string> output,
        string prefix,
        string continuation,
        List<string> lines,
        string? language,
        int columns,
        bool color)
    {
        language ??= string.Empty;
        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        var highlight = color && KnownLanguage(language)
            && lines.Count <= MaxHighlightLines
            && lines.Sum(static line => line.Length + 1) <= MaxHighlightBytes;
        var inBlockComment = false;
        var inTripleString = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var runs = highlight
                ? CodeRuns(lines[index], ref inBlockComment, ref inTripleString)
                : [new(lines[index], default)];
            AddRuns(output, Prefix(index == 0 ? prefix : continuation, runs), continuation, columns, color);
        }
    }

    private static List<Run> CodeRuns(string value, ref bool inBlockComment, ref bool inTripleString)
    {
        var runs = new List<Run>();
        for (var position = 0; position < value.Length;)
        {
            if (inBlockComment)
            {
                var end = value.IndexOf("*/", position, StringComparison.Ordinal);
                var length = end < 0 ? value.Length - position : end + 2 - position;
                Add(runs, value.Substring(position, length), new Style { Color = "90", Dim = true, Italic = true });
                position += length;
                inBlockComment = end < 0;
                continue;
            }

            if (inTripleString)
            {
                var end = value.IndexOf("\"\"\"", position, StringComparison.Ordinal);
                var length = end < 0 ? value.Length - position : end + 3 - position;
                Add(runs, value.Substring(position, length), new Style { Color = "32" });
                position += length;
                inTripleString = end < 0;
                continue;
            }

            if (value.AsSpan(position).StartsWith("//", StringComparison.Ordinal)
                || value[position] == '#')
            {
                Add(runs, value[position..], new Style { Color = "90", Dim = true, Italic = true });
                break;
            }

            if (value.AsSpan(position).StartsWith("/*", StringComparison.Ordinal))
            {
                inBlockComment = true;
                continue;
            }

            if (value.AsSpan(position).StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                inTripleString = true;
                Add(runs, "\"\"\"", new Style { Color = "32" });
                position += 3;
                continue;
            }

            if (value[position] is '\'' or '"' or '`')
            {
                var end = StringEnd(value, position, value[position]);
                Add(runs, value[position..end], new Style { Color = "32" });
                position = end;
                continue;
            }

            if (char.IsDigit(value[position]))
            {
                var end = position + 1;
                while (end < value.Length && (char.IsLetterOrDigit(value[end]) || value[end] is '.' or '_'))
                {
                    end++;
                }

                Add(runs, value[position..end], new Style { Color = "33" });
                position = end;
                continue;
            }

            if (char.IsLetter(value[position]) || value[position] == '_')
            {
                var end = position + 1;
                while (end < value.Length && (char.IsLetterOrDigit(value[end]) || value[end] == '_'))
                {
                    end++;
                }

                var word = value[position..end];
                var next = value.AsSpan(end).TrimStart();
                var style = Keywords.Contains(word)
                    ? new Style { Color = "35", Bold = true }
                    : next.StartsWith("(", StringComparison.Ordinal) ? new Style { Color = "36" } : default;
                Add(runs, word, style);
                position = end;
                continue;
            }

            var character = value[position].ToString(CultureInfo.InvariantCulture);
            var operatorStyle = "+-*/%=!<>&|^~?:".Contains(character, StringComparison.Ordinal)
                ? new Style { Color = "36" }
                : default;
            Add(runs, character, operatorStyle);
            position++;
        }

        return runs;
    }

    private static void AddRuns(
        List<string> output,
        List<Run> source,
        string continuation,
        int columns,
        bool color)
    {
        var rows = new List<List<Run>> { new() };
        var cells = 0;
        foreach (var run in source)
        {
            foreach (var rune in run.Text.EnumerateRunes())
            {
                var runeWidth = TerminalText.Width(rune);
                if (cells > 0 && cells + runeWidth > Math.Max(1, columns))
                {
                    rows.Add([new Run(continuation, default)]);
                    cells = TerminalText.Width(continuation);
                }

                Add(rows[^1], rune.ToString(), run.Style);
                cells += runeWidth;
            }
        }

        foreach (var row in rows)
        {
            var rendered = new StringBuilder();
            foreach (var run in row)
            {
                _ = rendered.Append(Ansi(run.Text, run.Style, color));
            }

            output.Add(rendered.ToString());
        }
    }

    private static string Ansi(string value, Style style, bool color)
    {
        if (!color || value.Length == 0)
        {
            return value;
        }

        style = new Style { Color = AssistantColor }.Merge(style);
        var codes = new List<string>();
        if (style.Bold)
        {
            codes.Add("1");
        }

        if (style.Dim)
        {
            codes.Add("2");
        }

        if (style.Italic)
        {
            codes.Add("3");
        }

        if (style.Underline)
        {
            codes.Add("4");
        }

        if (style.Strike)
        {
            codes.Add("9");
        }

        if (!string.IsNullOrEmpty(style.Color))
        {
            codes.Add(style.Color);
        }

        return $"\u001b[{string.Join(';', codes)}m{value}\u001b[0m";
    }

    private static bool TryTable(
        string[] lines,
        int index,
        out List<string[]> table,
        out int consumed)
    {
        table = [];
        consumed = 0;
        if (index + 1 >= lines.Length
            || !TableRow(lines[index], out var header)
            || !TableDelimiter(lines[index + 1], header.Length))
        {
            return false;
        }

        table.Add(header);
        var cursor = index + 2;
        while (cursor < lines.Length && TableRow(lines[cursor], out var row))
        {
            Array.Resize(ref row, header.Length);
            table.Add(row);
            cursor++;
        }

        consumed = cursor - index - 1;
        return true;
    }

    private static void AddTable(
        List<string> output,
        string prefix,
        string continuation,
        List<string[]> rows,
        int columns,
        bool color)
    {
        var widths = new int[rows[0].Length];
        foreach (var row in rows)
        {
            for (var index = 0; index < widths.Length; index++)
            {
                widths[index] = Math.Max(widths[index], TerminalText.Width(row[index] ?? string.Empty));
            }
        }

        var required = widths.Sum() + (3 * widths.Length) + 1 + TerminalText.Width(prefix);
        if (required > columns)
        {
            for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
            {
                for (var column = 0; column < widths.Length; column++)
                {
                    var linePrefix = rowIndex == 1 && column == 0 ? prefix : continuation;
                    var runs = Inline(rows[0][column] + ": ", new Style { Color = "36", Bold = true });
                    runs.AddRange(Inline(rows[rowIndex][column] ?? string.Empty, default));
                    AddRuns(output, Prefix(linePrefix, runs), continuation, columns, color);
                }
            }

            return;
        }

        AddTableBorder(output, prefix, widths, "┌", "┬", "┐", color);
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var runs = new List<Run> { new(continuation + "│", new Style { Color = "90", Dim = true }) };
            for (var column = 0; column < widths.Length; column++)
            {
                var cell = rows[rowIndex][column] ?? string.Empty;
                var basis = rowIndex == 0 ? new Style { Color = "36", Bold = true } : default;
                runs.AddRange(Inline(" " + cell, basis));
                Add(runs, new string(' ', widths[column] - TerminalText.Width(cell) + 1), default);
                Add(runs, "│", new Style { Color = "90", Dim = true });
            }

            AddRuns(output, runs, continuation, required, color);
            if (rowIndex == 0)
            {
                AddTableBorder(output, continuation, widths, "├", "┼", "┤", color);
            }
        }

        AddTableBorder(output, continuation, widths, "└", "┴", "┘", color);
    }

    private static void AddTableBorder(
        List<string> output,
        string prefix,
        int[] widths,
        string left,
        string middle,
        string right,
        bool color)
    {
        var text = new StringBuilder(prefix).Append(left);
        for (var index = 0; index < widths.Length; index++)
        {
            if (index > 0)
            {
                _ = text.Append(middle);
            }

            _ = text.Append('─', widths[index] + 2);
        }

        _ = text.Append(right);
        output.Add(Ansi(text.ToString(), new Style { Color = "90", Dim = true }, color));
    }

    private static bool TableRow(string value, out string[] cells)
    {
        var trimmed = value.Trim();
        if (!trimmed.Contains('|', StringComparison.Ordinal))
        {
            cells = [];
            return false;
        }

        cells = [.. trimmed.Trim('|').Split('|').Select(static cell => cell.Trim())];
        return cells.Length > 1;
    }

    private static bool TableDelimiter(string value, int count) =>
        TableRow(value, out var cells)
        && cells.Length == count
        && cells.All(static cell => cell.Trim(':').Length >= 3 && cell.Trim(':').All(static value => value == '-'));

    private static bool Heading(string value, out string heading)
    {
        var count = 0;
        while (count < Math.Min(6, value.Length) && value[count] == '#')
        {
            count++;
        }

        if (count == 0 || count >= value.Length || value[count] != ' ')
        {
            heading = string.Empty;
            return false;
        }

        heading = value[(count + 1)..].Trim();
        return true;
    }

    private static bool ListMarker(string value, out string marker, out string rest)
    {
        if (value.Length >= 2 && "-*+".Contains(value[0], StringComparison.Ordinal) && value[1] == ' ')
        {
            marker = "• ";
            rest = value[2..];
            if (rest.Length >= 4 && rest[0] == '[' && rest[2] == ']' && rest[3] == ' '
                && rest[1] is ' ' or 'x' or 'X')
            {
                marker += rest[1] == ' ' ? "☐ " : "☑ ";
                rest = rest[4..];
            }

            return true;
        }

        var digits = 0;
        while (digits < value.Length && char.IsDigit(value[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits + 1 < value.Length && value[digits] is '.' or ')' && value[digits + 1] == ' ')
        {
            marker = value[..(digits + 2)];
            rest = value[(digits + 2)..];
            return true;
        }

        marker = string.Empty;
        rest = string.Empty;
        return false;
    }

    private static bool ThematicBreak(string value)
    {
        var compact = string.Concat(value.Where(static character => !char.IsWhiteSpace(character)));
        return compact.Length >= 3 && "-*_".Contains(compact[0], StringComparison.Ordinal)
            && compact.All(character => character == compact[0]);
    }

    private static bool TryDelimited(string value, int position, string marker, out int end)
    {
        end = -1;
        if (!value.AsSpan(position).StartsWith(marker, StringComparison.Ordinal)
            || position + marker.Length >= value.Length
            || char.IsWhiteSpace(value[position + marker.Length]))
        {
            return false;
        }

        end = value.IndexOf(marker, position + marker.Length, StringComparison.Ordinal);
        return end > position + marker.Length && !char.IsWhiteSpace(value[end - 1]);
    }

    private static bool TryCode(string value, int position, out string code, out int end)
    {
        var markerLength = 1;
        while (position + markerLength < value.Length && value[position + markerLength] == '`')
        {
            markerLength++;
        }

        var close = value.IndexOf(new string('`', markerLength), position + markerLength, StringComparison.Ordinal);
        if (close < 0)
        {
            code = string.Empty;
            end = position;
            return false;
        }

        code = value[(position + markerLength)..close];
        if (code.Length > 1 && code.StartsWith(' ') && code.EndsWith(' ') && code.Trim().Length > 0)
        {
            code = code[1..^1];
        }

        end = close + markerLength;
        return true;
    }

    private static bool TryLink(
        string value,
        int position,
        out string label,
        out string url,
        out int end)
    {
        var labelEnd = value.IndexOf("](", position, StringComparison.Ordinal);
        var urlEnd = labelEnd < 0 ? -1 : value.IndexOf(')', labelEnd + 2);
        if (labelEnd <= position + 1 || urlEnd < 0)
        {
            label = string.Empty;
            url = string.Empty;
            end = position;
            return false;
        }

        label = value[(position + 1)..labelEnd];
        url = value[(labelEnd + 2)..urlEnd];
        end = urlEnd + 1;
        return true;
    }

    private static bool TryFence(string value, out (char Marker, int Length, string Language) fence)
    {
        var trimmed = value.TrimStart(' ');
        var indent = value.Length - trimmed.Length;
        if (indent > 3 || trimmed.Length < 3 || trimmed[0] is not ('`' or '~'))
        {
            fence = default;
            return false;
        }

        var marker = trimmed[0];
        var length = 0;
        while (length < trimmed.Length && trimmed[length] == marker)
        {
            length++;
        }

        if (length < 3)
        {
            fence = default;
            return false;
        }

        var info = trimmed[length..].Trim();
        var language = info.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        language = language.Trim('{', '}', '.').ToLowerInvariant();
        fence = (marker, length, language);
        return true;
    }

    private static bool IsFenceClose(string value, char marker, int minimum)
    {
        var trimmed = value.TrimStart(' ');
        if (value.Length - trimmed.Length > 3)
        {
            return false;
        }

        var count = 0;
        while (count < trimmed.Length && trimmed[count] == marker)
        {
            count++;
        }

        return count >= minimum && trimmed[count..].Trim().Length == 0;
    }

    private static bool KnownLanguage(string language)
    {
        language = language switch
        {
            "c-sharp" or "c#" => "csharp",
            "golang" => "go",
            "python3" or "py3" => "python",
            "shell" or "sh" or "zsh" => "bash",
            _ => language,
        };
        return language is "bash" or "c" or "cpp" or "csharp" or "css" or "diff" or "go" or "html"
            or "java" or "javascript" or "js" or "json" or "kotlin" or "php" or "python" or "ruby"
            or "rust" or "sql" or "swift" or "typescript" or "ts" or "xml" or "yaml" or "yml";
    }

    private static int StringEnd(string value, int start, char quote)
    {
        var escaped = false;
        for (var position = start + 1; position < value.Length; position++)
        {
            if (!escaped && value[position] == quote)
            {
                return position + 1;
            }

            escaped = !escaped && value[position] == '\\';
            if (value[position] != '\\')
            {
                escaped = false;
            }
        }

        return value.Length;
    }

    private static bool IsEscapable(char value) => char.IsPunctuation(value) || char.IsSymbol(value);

    private static List<Run> Prefix(string prefix, List<Run> runs) =>
        prefix.Length == 0 ? runs : Join([new Run(prefix, default)], runs);

    private static List<Run> Join(List<Run> left, List<Run> right)
    {
        foreach (var run in right)
        {
            Add(left, run.Text, run.Style);
        }

        return left;
    }

    private static void Add(List<Run> runs, string text, Style style)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (runs.Count > 0 && runs[^1].Style == style)
        {
            runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
            return;
        }

        runs.Add(new Run(text, style));
    }

    private readonly record struct Run(string Text, Style Style);

    private readonly record struct Style
    {
        public string Color { get; init; }

        public bool Bold { get; init; }

        public bool Dim { get; init; }

        public bool Italic { get; init; }

        public bool Underline { get; init; }

        public bool Strike { get; init; }

        public Style Merge(Style overlay) => new()
        {
            Color = string.IsNullOrEmpty(overlay.Color) ? Color ?? string.Empty : overlay.Color,
            Bold = Bold || overlay.Bold,
            Dim = Dim || overlay.Dim,
            Italic = Italic || overlay.Italic,
            Underline = Underline || overlay.Underline,
            Strike = Strike || overlay.Strike,
        };
    }
}
