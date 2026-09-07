using System.Text;

namespace Parrot.Cli.Enhanced;

internal readonly record struct MarqueeValue(string Prefix, string Text, int Frame) : ILiveBufferItem
{
    private const int SeparatorCells = 3;

    public ILiveBufferItem Animate(int frame) => this with { Frame = frame };

    public ILiveBufferItem CaptureAnimation(int frame) => this with { Frame = frame };

    public ILiveBufferItem AnimateSinceCapture(int frame) => this with { Frame = frame - Frame };

    public MultiLine Render(LiveBufferRenderContext context)
    {
        var prefix = SingleLine(Prefix);
        var text = SingleLine(Text);
        var prefixWidth = TerminalText.Width(prefix);
        var available = Math.Max(0, context.Columns - prefixWidth);
        var rendered = available == 0
            ? TerminalText.Clip(prefix, context.Columns)
            : prefix + Viewport(text, available);
        return new MultiLine(
            [new TerminalLine(rendered, context.Palette.LiveSurface)],
            null,
            LiveBufferRetention.Tail);
    }

    private static string SingleLine(string value) => TerminalText.Sanitize(value)
        .Replace("\n", " ", StringComparison.Ordinal);

    private string Viewport(string text, int width)
    {
        var cells = Cells(text);
        if (cells.Count <= width)
        {
            return text;
        }

        cells.AddRange(Enumerable.Repeat<string?>(" ", SeparatorCells));
        var offset = Frame % cells.Count;
        if (offset < 0)
        {
            offset += cells.Count;
        }

        var result = new StringBuilder(width);
        for (var column = 0; column < width;)
        {
            var cell = cells[(offset + column) % cells.Count];
            if (cell is null)
            {
                _ = result.Append(' ');
                column++;
                continue;
            }

            var cellWidth = TerminalText.Width(cell);
            if (cellWidth > width - column)
            {
                _ = result.Append(' ', width - column);
                break;
            }

            _ = result.Append(cell);
            column += cellWidth;
        }

        return result.ToString();
    }

    private static List<string?> Cells(string value)
    {
        var cells = new List<string?>();
        foreach (var element in TerminalText.EnumerateGraphemes(value))
        {
            var width = TerminalText.Width(element);
            if (width == 0)
            {
                if (cells.Count > 0 && cells[^1] is { } previous)
                {
                    cells[^1] = previous + element;
                }

                continue;
            }

            cells.Add(element);
            for (var index = 1; index < width; index++)
            {
                cells.Add(null);
            }
        }

        return cells;
    }
}
