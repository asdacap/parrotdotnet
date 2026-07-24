using System.Threading.Channels;

namespace Parrot.Cli;

internal sealed class TerminalFrameRenderer(TextWriter output, Func<int> columns, TerminalPalette palette)
{
    private const string DisableAutowrap = "\u001b[?7l";
    private const string EnableAutowrap = "\u001b[?7h";

    private readonly Channel<bool> _drawing = CreateDrawingGate();
    private int _caretRow;
    private int _height;

    public async Task Draw(TerminalFrame frame, CancellationToken cancellationToken)
    {
        _ = await _drawing.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ClearFrame(CancellationToken.None).ConfigureAwait(false);

            var width = Math.Max(1, columns());
            var prompt = frame.Prompt.Sanitize();
            var promptRows = Layout(prompt.Prefix + prompt.Text, width);
            var (cursorRow, cursorCells) = Cursor(prompt, width);
            var rows = frame.Rows
                .SelectMany(value => Layout(TerminalText.Sanitize(value), width))
                .Select(value => new RenderedRow(value, palette.LiveSurface))
                .ToList();
            if (frame.Spinner is { } spinner)
            {
                rows.Add(new RenderedRow(spinner.Render(), palette.Marker));
            }

            var rowsBeforePrompt = rows.Count + 1;
            rows.Add(new RenderedRow(frame.Modeline.Render(width), palette.Modeline));
            rows.AddRange(promptRows.Select(value => new RenderedRow(value, palette.Prompt)));

            await output.WriteAsync($"\u001b[?25l{DisableAutowrap}".AsMemory(), CancellationToken.None)
                .ConfigureAwait(false);
            for (var row = 0; row < rows.Count; row++)
            {
                await output.WriteAsync(
                    palette.LiveBackground.Apply("\u001b[2K").AsMemory(), CancellationToken.None)
                    .ConfigureAwait(false);
                await output.WriteAsync(rows[row].Style.Apply(rows[row].Text).AsMemory(), CancellationToken.None)
                    .ConfigureAwait(false);
                if (row < rows.Count - 1)
                {
                    await output.WriteAsync("\r\n".AsMemory(), CancellationToken.None).ConfigureAwait(false);
                }
            }

            _height = rows.Count;
            _caretRow = rowsBeforePrompt + cursorRow;
            var lastRow = rows.Count - 1;
            if (lastRow > _caretRow)
            {
                await output.WriteAsync($"\u001b[{lastRow - _caretRow}A".AsMemory(), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await output.WriteAsync("\r".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            if (cursorCells > 0)
            {
                await output.WriteAsync($"\u001b[{cursorCells}C".AsMemory(), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await output.WriteAsync($"{EnableAutowrap}\u001b[?25h".AsMemory(), CancellationToken.None)
                .ConfigureAwait(false);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _drawing.Writer.WriteAsync(true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task Clear(CancellationToken cancellationToken)
    {
        _ = await _drawing.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ClearFrame(CancellationToken.None).ConfigureAwait(false);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _drawing.Writer.WriteAsync(true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task FlushActivities(IReadOnlyList<string> activities, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activities);

        _ = await _drawing.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ClearFrame(CancellationToken.None).ConfigureAwait(false);
            foreach (var activity in activities)
            {
                var clean = TerminalText.Sanitize(activity);
                await output.WriteAsync(palette.Muted.Apply(clean).AsMemory(), CancellationToken.None)
                    .ConfigureAwait(false);
                await output.WriteAsync("\r\n".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            }

            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _drawing.Writer.WriteAsync(true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static Channel<bool> CreateDrawingGate()
    {
        var gate = Channel.CreateBounded<bool>(1);
        if (!gate.Writer.TryWrite(true))
        {
            throw new InvalidOperationException("Unable to initialize the drawing gate.");
        }

        return gate;
    }

    private static List<string> Layout(string value, int width)
    {
        var rows = new List<string>();
        var row = new System.Text.StringBuilder();
        var cells = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                rows.Add(row.ToString());
                _ = row.Clear();
                cells = 0;
                continue;
            }

            var runeWidth = TerminalText.Width(rune);
            if (cells > 0 && cells + runeWidth > width)
            {
                rows.Add(row.ToString());
                _ = row.Clear();
                cells = 0;
            }

            _ = row.Append(rune);
            cells += runeWidth;
        }

        rows.Add(row.ToString());
        return rows;
    }

    private static (int Row, int Cells) Cursor(PromptValue prompt, int width)
    {
        var before = prompt.Prefix + string.Concat(prompt.Text.EnumerateRunes().Take(prompt.Cursor));
        var rows = Layout(before, width);
        return (rows.Count - 1, TerminalText.Width(rows[^1]));
    }

    private async Task ClearFrame(CancellationToken cancellationToken)
    {
        if (_height == 0)
        {
            return;
        }

        await output.WriteAsync($"\u001b[?25l{DisableAutowrap}".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
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
                await output.WriteAsync("\r\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        if (_height > 1)
        {
            await output.WriteAsync($"\u001b[{_height - 1}A".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await output.WriteAsync($"\r{EnableAutowrap}\u001b[?25h".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        _height = 0;
        _caretRow = 0;
    }

    private readonly record struct RenderedRow(string Text, TerminalStyle Style);
}
