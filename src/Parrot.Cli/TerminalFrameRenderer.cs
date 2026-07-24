namespace Parrot.Cli;

internal sealed class TerminalFrameRenderer(TextWriter output, Func<int> columns, TerminalPalette palette)
{
    private int _caretRow;
    private int _height;

    public async Task Draw(TerminalFrame frame, CancellationToken cancellationToken)
    {
        await Clear(cancellationToken).ConfigureAwait(false);

        var width = Math.Max(1, columns());
        var prompt = frame.Prompt.Sanitize();
        var promptLines = (prompt.Prefix + prompt.Text).Split('\n');
        var (cursorRow, cursorCells) = Cursor(prompt);
        var rows = frame.Rows.Select(value => palette.LiveSurface.Apply(TerminalText.Sanitize(value))).ToList();
        rows.Add(palette.Modeline.Apply(frame.Modeline.Render(width)));
        rows.AddRange(promptLines.Select(palette.Prompt.Apply));

        await output.WriteAsync("\u001b[?25l".AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(string.Join('\n', rows).AsMemory(), cancellationToken).ConfigureAwait(false);

        _height = rows.Count;
        _caretRow = frame.Rows.Count + 1 + cursorRow;
        var lastRow = rows.Count - 1;
        if (lastRow > _caretRow)
        {
            await output.WriteAsync($"\u001b[{lastRow - _caretRow}A".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        await output.WriteAsync("\r".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (cursorCells > 0)
        {
            await output.WriteAsync($"\u001b[{cursorCells}C".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await output.WriteAsync("\u001b[?25h".AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task Clear(CancellationToken cancellationToken)
    {
        if (_height == 0)
        {
            return;
        }

        await output.WriteAsync("\u001b[?25l".AsMemory(), cancellationToken).ConfigureAwait(false);
        var rowsBelowCaret = _height - _caretRow - 1;
        if (rowsBelowCaret > 0)
        {
            await output.WriteAsync($"\u001b[{rowsBelowCaret}B".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        await output.WriteAsync("\r".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (_height > 1)
        {
            await output.WriteAsync($"\u001b[{_height - 1}A".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        for (var row = 0; row < _height; row++)
        {
            await output.WriteAsync("\u001b[2K".AsMemory(), cancellationToken).ConfigureAwait(false);
            if (row < _height - 1)
            {
                await output.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        if (_height > 1)
        {
            await output.WriteAsync($"\u001b[{_height - 1}A".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await output.WriteAsync("\r\u001b[?25h".AsMemory(), cancellationToken).ConfigureAwait(false);
        _height = 0;
        _caretRow = 0;
    }

    private static (int Row, int Cells) Cursor(PromptValue prompt)
    {
        var before = prompt.Prefix + string.Concat(prompt.Text.EnumerateRunes().Take(prompt.Cursor));
        var lines = before.Split('\n');
        return (lines.Length - 1, TerminalText.Width(lines[^1]));
    }
}
