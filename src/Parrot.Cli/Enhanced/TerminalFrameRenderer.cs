using System.Threading.Channels;

namespace Parrot.Cli.Enhanced;

internal sealed class TerminalFrameRenderer(
    TextWriter output,
    Func<int> columns,
    TerminalPalette palette,
    int maxLiveRows,
    int maxInputRows)
{
    internal const int DefaultInputRows = 12;
    internal const int DefaultLiveRows = 10;

    private const string DisableAutowrap = "\u001b[?7l";
    private const string EnableAutowrap = "\u001b[?7h";

    private readonly Channel<bool> _drawing = CreateDrawingGate();
    private readonly TerminalSurface _surface = new(1, 1);
    private int _caretRow;
    private TerminalFrame? _frame;
    private int _renderedHeight;
    private PromptValue? _prompt;

    public async Task Draw(TerminalFrame frame, CancellationToken cancellationToken)
    {
        _ = await _drawing.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = frame with { Prompt = _prompt ?? frame.Prompt };
            _prompt = current.Prompt;
            var availableRows = Math.Max(1, _renderedHeight);
            await ClearFrame(CancellationToken.None).ConfigureAwait(false);
            await DrawFrame(current, availableRows, CancellationToken.None).ConfigureAwait(false);
            _frame = current;
        }
        finally
        {
            await _drawing.Writer.WriteAsync(true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task UpdateRows(IReadOnlyList<string> rowsToDraw, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rowsToDraw);

        _ = await _drawing.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_frame is not { } frame)
            {
                return;
            }

            var updated = frame with { Rows = rowsToDraw };
            var availableRows = Math.Max(1, _renderedHeight);
            await ClearFrame(CancellationToken.None).ConfigureAwait(false);
            await DrawFrame(updated, availableRows, CancellationToken.None).ConfigureAwait(false);
            _frame = updated;
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
            _frame = null;
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _drawing.Writer.WriteAsync(true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public Task FlushActivities(IReadOnlyList<string> activities, CancellationToken cancellationToken) =>
        CommitScrollback([.. activities.Select(value => palette.Muted.Apply(TerminalText.Sanitize(value)))], cancellationToken);

    public async Task FlushActivitiesAndDraw(
        IReadOnlyList<string> activities,
        TerminalFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activities);

        _ = await _drawing.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var availableRows = activities.Count == 0 ? Math.Max(1, _renderedHeight) : 1;
            await ClearFrame(CancellationToken.None).ConfigureAwait(false);
            await WriteActivities(activities, CancellationToken.None).ConfigureAwait(false);
            await DrawFrame(frame, availableRows, CancellationToken.None).ConfigureAwait(false);
            _frame = frame;
        }
        finally
        {
            await _drawing.Writer.WriteAsync(true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task CommitUserMessage(string prefix, string text, CancellationToken cancellationToken)
    {
        var clean = TerminalText.Sanitize(prefix + text).TrimEnd('\r', '\n');
        var width = Math.Max(1, columns());
        var committed = Layout(clean, width).Select(palette.User.Apply).ToList();
        await CommitScrollback(committed, cancellationToken).ConfigureAwait(false);
    }

    public async Task CommitScrollback(IReadOnlyList<string> scrollback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scrollback);

        _ = await _drawing.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var availableRows = scrollback.Count == 0 ? Math.Max(1, _renderedHeight) : 1;
            await ClearFrame(CancellationToken.None).ConfigureAwait(false);
            foreach (var line in scrollback)
            {
                await output.WriteAsync(line.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                await output.WriteAsync("\r\n".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            }

            if (_frame is { } frame)
            {
                await DrawFrame(frame, availableRows, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await _drawing.Writer.WriteAsync(true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task UpdatePrompt(PromptValue prompt, CancellationToken cancellationToken)
    {
        _ = await _drawing.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _prompt = prompt;
            if (_frame is not { } frame)
            {
                return;
            }

            var updated = frame with { Prompt = prompt };
            var availableRows = Math.Max(1, _renderedHeight);
            await ClearFrame(CancellationToken.None).ConfigureAwait(false);
            await DrawFrame(updated, availableRows, CancellationToken.None).ConfigureAwait(false);
            _frame = updated;
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

    private async Task WriteActivities(IReadOnlyList<string> activities, CancellationToken cancellationToken)
    {
        foreach (var activity in activities)
        {
            var clean = TerminalText.Sanitize(activity);
            await output.WriteAsync(palette.Muted.Apply(clean).AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.WriteAsync("\r\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DrawFrame(TerminalFrame frame, int availableRows, CancellationToken cancellationToken)
    {
        var width = Math.Max(1, columns());
        var prompt = frame.Prompt.Sanitize();
        var promptRows = Layout(prompt.Prefix + prompt.Text, width);
        var (cursorRow, cursorCells) = Cursor(prompt, width);
        var inputLimit = Math.Max(1, maxInputRows);
        if (promptRows.Count > inputLimit)
        {
            var promptStart = Math.Clamp(cursorRow - inputLimit + 1, 0, promptRows.Count - inputLimit);
            promptRows = promptRows.GetRange(promptStart, inputLimit);
            cursorRow -= promptStart;
        }

        var liveRows = frame.Rows
            .SelectMany(value => Layout(TerminalText.Sanitize(value), width))
            .Select(value => new RenderedRow(value, palette.LiveSurface))
            .ToList();
        if (frame.Spinner is { } spinner)
        {
            liveRows.Add(new RenderedRow(spinner.Render(), palette.Marker));
        }

        var content = liveRows.TakeLast(Math.Max(1, maxLiveRows)).ToList();
        content.Add(new RenderedRow(frame.Modeline.Render(width), palette.Modeline));
        var promptStartRow = content.Count;
        content.AddRange(promptRows.Select(value => new RenderedRow(value, palette.Prompt)));

        _renderedHeight = content.Count;
        _caretRow = promptStartRow + cursorRow;
        _surface.Resize(width, _renderedHeight);
        _surface.Clear();
        for (var row = 0; row < content.Count; row++)
        {
            _surface.Write(row, 0, content[row].Text, content[row].Style);
        }

        await output.WriteAsync($"\u001b[?25l{DisableAutowrap}".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        var rowsToReserve = Math.Max(0, _renderedHeight - availableRows);
        for (var row = 0; row < rowsToReserve; row++)
        {
            await output.WriteAsync("\r\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        if (rowsToReserve > 0)
        {
            await output.WriteAsync($"\u001b[{rowsToReserve}A\r".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        for (var row = 0; row < _renderedHeight; row++)
        {
            await output.WriteAsync("\u001b[2K".AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(_surface.RenderRow(row, palette.LiveBackground).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (row < _renderedHeight - 1)
            {
                await output.WriteAsync("\r\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        var lastRow = _renderedHeight - 1;
        if (lastRow > _caretRow)
        {
            await output.WriteAsync($"\u001b[{lastRow - _caretRow}A".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        await output.WriteAsync("\r".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (cursorCells > 0)
        {
            await output.WriteAsync($"\u001b[{Math.Min(cursorCells, width - 1)}C".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        await output.WriteAsync($"{EnableAutowrap}\u001b[?25h".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearFrame(CancellationToken cancellationToken)
    {
        if (_renderedHeight == 0)
        {
            return;
        }

        await output.WriteAsync($"\u001b[?25l{DisableAutowrap}".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        var rowsBelowCaret = _renderedHeight - _caretRow - 1;
        if (rowsBelowCaret > 0)
        {
            await output.WriteAsync($"\u001b[{rowsBelowCaret}B".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        await output.WriteAsync("\r".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (_renderedHeight > 1)
        {
            await output.WriteAsync($"\u001b[{_renderedHeight - 1}A".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        for (var row = 0; row < _renderedHeight; row++)
        {
            await output.WriteAsync("\u001b[2K".AsMemory(), cancellationToken).ConfigureAwait(false);
            if (row < _renderedHeight - 1)
            {
                await output.WriteAsync("\r\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        if (_renderedHeight > 1)
        {
            await output.WriteAsync($"\u001b[{_renderedHeight - 1}A".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        await output.WriteAsync($"\r{EnableAutowrap}\u001b[?25h".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        _renderedHeight = 0;
        _caretRow = 0;
    }

    private readonly record struct RenderedRow(string Text, TerminalStyle Style);
}
