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

    public async Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        _ = await _drawing.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (frame, width) = Render(items);
            var availableRows = Math.Max(1, _frame?.Lines.Count ?? 0);
            await ClearFrame(CancellationToken.None).ConfigureAwait(false);
            await DrawFrame(frame, width, availableRows, CancellationToken.None).ConfigureAwait(false);
            _frame = frame;
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

    public async Task Commit(
        IReadOnlyList<string> scrollback,
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scrollback);
        ArgumentNullException.ThrowIfNull(items);

        _ = await _drawing.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (frame, width) = Render(items);
            var availableRows = scrollback.Count == 0 ? Math.Max(1, _renderedHeight) : 1;
            await ClearFrame(CancellationToken.None).ConfigureAwait(false);
            foreach (var line in scrollback)
            {
                await output.WriteAsync(line.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                await output.WriteAsync("\r\n".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            }

            await DrawFrame(frame, width, availableRows, CancellationToken.None).ConfigureAwait(false);
            _frame = frame;
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

    private (TerminalFrame Frame, int Width) Render(IReadOnlyList<ILiveBufferItem> items)
    {
        var width = Math.Max(1, columns());
        var context = new LiveBufferRenderContext(width, palette);
        var rendered = items.Select(item => item.Render(context)).ToList();
        var carets = rendered.Where(value => value.Caret is not null).ToList();
        if (carets.Count != 1)
        {
            throw new InvalidOperationException("The live buffer must contain exactly one caret.");
        }

        var tailRows = rendered
            .Where(value => value.Retention == LiveBufferRetention.Tail)
            .Sum(value => value.Lines.Count);
        var tailToSkip = Math.Max(0, tailRows - Math.Max(1, maxLiveRows));
        var lines = new List<TerminalLine>();
        LiveBufferCaret? caret = null;

        foreach (var item in rendered)
        {
            var itemCaret = item.Caret;
            if (itemCaret is { } position
                && (position.Row < 0 || position.Row >= item.Lines.Count))
            {
                throw new InvalidOperationException("The live buffer caret is outside its item.");
            }

            var start = 0;
            var count = item.Lines.Count;
            if (item.Retention == LiveBufferRetention.Tail)
            {
                var skipped = Math.Min(tailToSkip, count);
                start = skipped;
                count -= skipped;
                tailToSkip -= skipped;
            }
            else if (item.Retention == LiveBufferRetention.Caret && count > Math.Max(1, maxInputRows))
            {
                if (itemCaret is not { } caretPosition)
                {
                    throw new InvalidOperationException("Caret-retained live buffer content requires a caret.");
                }

                var limit = Math.Max(1, maxInputRows);
                start = Math.Clamp(caretPosition.Row - limit + 1, 0, count - limit);
                count = limit;
            }

            if (itemCaret is { } currentCaret)
            {
                if (currentCaret.Row < start || currentCaret.Row >= start + count)
                {
                    throw new InvalidOperationException("The live buffer caret was removed by retention.");
                }

                caret = new LiveBufferCaret(
                    lines.Count + currentCaret.Row - start,
                    Math.Clamp(currentCaret.Cells, 0, width - 1));
            }

            for (var index = start; index < start + count; index++)
            {
                var line = item.Lines[index];
                var text = TerminalText.Sanitize(line.Text).Replace("\n", string.Empty, StringComparison.Ordinal);
                lines.Add(new TerminalLine(text, line.Style));
            }
        }

        return (new TerminalFrame(
            lines,
            caret ?? throw new InvalidOperationException("The live buffer has no caret.")), width);
    }

    private async Task DrawFrame(
        TerminalFrame frame,
        int width,
        int availableRows,
        CancellationToken cancellationToken)
    {
        _renderedHeight = frame.Lines.Count;
        _caretRow = frame.Caret.Row;
        _surface.Resize(width, _renderedHeight);
        _surface.Clear();
        for (var row = 0; row < frame.Lines.Count; row++)
        {
            _surface.Write(row, 0, frame.Lines[row].Text, frame.Lines[row].Style);
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
        if (frame.Caret.Cells > 0)
        {
            await output.WriteAsync($"\u001b[{frame.Caret.Cells}C".AsMemory(), cancellationToken)
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
}
