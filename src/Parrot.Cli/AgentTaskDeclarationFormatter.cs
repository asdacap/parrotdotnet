using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli;

internal static class AgentTaskDeclarationFormatter
{
    private const int InlineValueWidth = 80;

    public static IReadOnlyList<string> Format(IReadOnlyList<PlanTaskDeclaration> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        return [.. Render(declarations).Select(static line => line.Text)];
    }

    public static IReadOnlyList<string> FormatForWidth(
        IReadOnlyList<PlanTaskDeclaration> declarations,
        int columns)
    {
        ArgumentNullException.ThrowIfNull(declarations);

        var width = Math.Max(1, columns);
        var result = new List<string>();
        foreach (var line in Render(declarations))
        {
            var indent = line.HangingIndent;
            var layoutWidth = Math.Max(width, TerminalText.Width(indent) + 1);
            result.AddRange(TerminalText.LayoutWordsHanging(line.Text, layoutWidth, indent));
        }

        return result;
    }

    private static List<RenderedLine> Render(IReadOnlyList<PlanTaskDeclaration> declarations)
    {
        var lines = new List<RenderedLine> { new("Agent tasks:", string.Empty) };
        Append(declarations, string.Empty, lines);
        return lines;
    }

    private static void Append(
        IReadOnlyList<PlanTaskDeclaration> declarations,
        string itemIndent,
        List<RenderedLine> lines)
    {
        foreach (var declaration in declarations)
        {
            var name = InlineValue(declaration.Name);
            lines.Add(new RenderedLine($"{itemIndent}- name: {name}", itemIndent + "  "));
            AppendScalar(lines, itemIndent + "  ", "description", declaration.Description);
            if (declaration.PayloadCase == PlanTaskDeclaration.PayloadOneofCase.Children)
            {
                Append(declaration.Children.Tasks, itemIndent + "    ", lines);
            }
        }
    }

    private static void AppendScalar(
        List<RenderedLine> lines,
        string fieldIndent,
        string field,
        string value)
    {
        var sanitized = TerminalText.Sanitize(value);
        var physicalLines = sanitized.Split('\n');
        var continuationIndent = fieldIndent + "  ";
        if (physicalLines.Length == 1 &&
            physicalLines[0].Length > 0 &&
            TerminalText.Width(physicalLines[0]) <= InlineValueWidth)
        {
            lines.Add(new RenderedLine($"{fieldIndent}{field}: {physicalLines[0]}", continuationIndent));
            return;
        }

        lines.Add(new RenderedLine($"{fieldIndent}{field}:", continuationIndent));
        foreach (var physicalLine in physicalLines)
        {
            if (physicalLine.Length == 0 && physicalLines.Length == 1)
            {
                continue;
            }

            lines.Add(new RenderedLine(continuationIndent + physicalLine, continuationIndent));
        }
    }

    private static string InlineValue(string value) =>
        TerminalText.Sanitize(value).Replace('\n', ' ');

    private readonly record struct RenderedLine(string Text, string HangingIndent);
}
