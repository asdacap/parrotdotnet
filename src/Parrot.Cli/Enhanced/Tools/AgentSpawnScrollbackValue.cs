namespace Parrot.Cli.Enhanced.Tools;

internal sealed class AgentSpawnScrollbackValue(string name, string metadata, string prompt) : IScrollbackItem
{
    public bool IsCompleted => true;

    public ScrollbackLayout Layout => ScrollbackLayout.Block;

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context)
    {
        const string prefix = "Started ";
        var columns = context.Decoration.ContentColumns(context.Columns);
        var heading = name.Length == 0 ? "Started agent" : prefix + TerminalText.SanitizeLine(name);
        var bold = new TerminalStyle(context.Palette.ColorEnabled ? "\u001b[1m" : string.Empty);
        var lines = new List<string>();
        var offset = 0;
        foreach (var line in TerminalText.LayoutWords(heading, columns))
        {
            offset = heading.IndexOf(line, offset, StringComparison.Ordinal);
            var plainLength = Math.Clamp(prefix.Length - offset, 0, line.Length);
            lines.Add(name.Length == 0 ? line : line[..plainLength] + bold.Apply(line[plainLength..]));
            offset += line.Length;
        }

        if (metadata.Length > 0)
        {
            lines.AddRange(TerminalText.LayoutWords(TerminalText.SanitizeLine(metadata), columns)
                .Select(context.Palette.Muted.Apply));
        }

        lines.Add(string.Empty);
        lines.AddRange(TerminalText.LayoutWords(TerminalText.Sanitize(prompt), columns));
        return context.Decoration.Apply("♟", lines);
    }
}
