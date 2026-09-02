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
            result.AddRange(TerminalText.LayoutHanging(line.Text, layoutWidth, indent));
        }

        return result;
    }

    private static List<RenderedLine> Render(IReadOnlyList<PlanTaskDeclaration> declarations)
    {
        var lines = new List<RenderedLine> { new("Agent tasks:", string.Empty) };
        Append(declarations, string.Empty, null, lines);
        return lines;
    }

    private static void Append(
        IReadOnlyList<PlanTaskDeclaration> declarations,
        string itemIndent,
        string? previousSiblingName,
        List<RenderedLine> lines)
    {
        for (var index = 0; index < declarations.Count; index++)
        {
            var declaration = declarations[index];
            var name = Scalar(declaration.Name);
            lines.Add(new RenderedLine($"{itemIndent}- name: {name}", itemIndent + "  "));
            lines.Add(new RenderedLine(
                $"{itemIndent}  status: {declaration.Status.ToString().ToLowerInvariant()}",
                itemIndent + "    "));

            if (ShouldRenderDependencies(declaration, previousSiblingName))
            {
                lines.Add(new RenderedLine(
                    $"{itemIndent}  dependencies: [{string.Join(", ", declaration.Dependencies.Select(InlineScalar))}]",
                    itemIndent + "    "));
            }

            AppendScalar(lines, itemIndent + "  ", "description", declaration.Description);
            if (declaration.PayloadCase == PlanTaskDeclaration.PayloadOneofCase.Children)
            {
                lines.Add(new RenderedLine($"{itemIndent}  payload:", itemIndent + "    "));
                Append(declaration.Children.Tasks, itemIndent + "    ", null, lines);
            }
            else
            {
                AppendScalar(lines, itemIndent + "  ", "payload", declaration.Instruction);
            }

            AppendScalar(lines, itemIndent + "  ", "acceptance_criteria", declaration.AcceptanceCriteria);
            if (declaration.HasModel)
            {
                lines.Add(new RenderedLine(
                    $"{itemIndent}  model: {InlineScalar(declaration.Model)}",
                    itemIndent + "    "));
            }

            previousSiblingName = declaration.Name;
        }
    }

    private static bool ShouldRenderDependencies(
        PlanTaskDeclaration declaration,
        string? previousSiblingName) =>
        declaration.Dependencies.Count > 0 &&
        (declaration.Dependencies.Count != 1 || declaration.Dependencies[0] != previousSiblingName);

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

    private static string Scalar(string value) => InlineScalar(TerminalText.Sanitize(value));

    private static string InlineScalar(string value) =>
        TerminalText.Sanitize(value).Replace('\n', ' ');

    private readonly record struct RenderedLine(string Text, string HangingIndent);
}
