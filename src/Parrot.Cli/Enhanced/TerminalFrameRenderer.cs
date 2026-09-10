using System.Threading.Channels;

namespace Parrot.Cli.Enhanced;

internal sealed class TerminalFrameRenderer(
    TextWriter output,
    Func<int> columns,
    TerminalPalette palette,
    int maxLiveRows,
    int maxInputRows,
    bool inlineDiff)
{
    internal const int DefaultInputRows = 12;

    private const string DisableAutowrap = "\u001b[?7l";
    private const string EnableAutowrap = "\u001b[?7h";

    private readonly Channel<bool> _drawing = CreateDrawingGate();
    private readonly TerminalSurface _surface = new(1, 1);
    private IScrollbackItem? _activeScrollback;
    private int _caretRow;
    private bool _committed;
    private bool _committedGap;
    private TerminalFrame? _frame;
    private ScrollbackLayout _lastLayout;
    private List<IScrollbackItem> _pendingScrollback = [];
    private int _renderedHeight;
    private int _renderedWidth;

    public async Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        _ = await _drawing.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var width = Math.Max(1, columns());
            var frame = Render(items, width);
            if (_frame is null)
            {
                await DrawFrame(frame, width, 1, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await ReplaceFrame(frame, width, CancellationToken.None).ConfigureAwait(false);
            }

            _frame = frame;
            _renderedWidth = width;
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
            _renderedWidth = 0;
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _drawing.Writer.WriteAsync(true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task Commit(
        IScrollbackItem scrollback,
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scrollback);
        ArgumentNullException.ThrowIfNull(items);

        _ = await _drawing.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var width = Math.Max(1, columns());
            var frame = Render(items, width);
            var context = new ScrollbackRenderContext(width, palette, inlineDiff);
            var active = _activeScrollback;
            var pending = new List<IScrollbackItem>(_pendingScrollback);
            var emitted = new List<IScrollbackItem>();

            if (active is null || scrollback.Continues(active))
            {
                Emit(scrollback, emitted, pending, ref active);
            }
            else
            {
                pending.Add(scrollback);
            }

            var lines = RenderScrollback(emitted, context);
            var availableRows = lines.Count == 0 ? Math.Max(1, _renderedHeight) : 1;
            _activeScrollback = active;
            _pendingScrollback = pending;
            await ClearFrame(CancellationToken.None).ConfigureAwait(false);
            foreach (var line in lines)
            {
                await output.WriteAsync(line.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                await output.WriteAsync("\r\n".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            }

            await DrawFrame(frame, width, availableRows, CancellationToken.None).ConfigureAwait(false);
            _frame = frame;
            _renderedWidth = width;
        }
        finally
        {
            await _drawing.Writer.WriteAsync(true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void Emit(
        IScrollbackItem item,
        List<IScrollbackItem> emitted,
        List<IScrollbackItem> pending,
        ref IScrollbackItem? active)
    {
        emitted.Add(item);
        active = item.IsCompleted ? null : item;
        while (true)
        {
            if (active is null)
            {
                if (pending.Count == 0)
                {
                    return;
                }

                var next = pending[0];
                pending.RemoveAt(0);
                emitted.Add(next);
                active = next.IsCompleted ? null : next;
                continue;
            }

            var current = active;
            var continuation = pending.FindIndex(value => value.Continues(current));
            if (continuation < 0)
            {
                return;
            }

            var nextContinuation = pending[continuation];
            pending.RemoveAt(continuation);
            emitted.Add(nextContinuation);
            active = nextContinuation.IsCompleted ? null : nextContinuation;
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

    private static List<int> ChangedRows(TerminalFrame previous, TerminalFrame current, bool repaintAll)
    {
        var changed = new List<int>();
        for (var row = 0; row < current.Lines.Count; row++)
        {
            if (repaintAll || row >= previous.Lines.Count || previous.Lines[row] != current.Lines[row])
            {
                changed.Add(row);
            }
        }

        return changed;
    }

    private List<string> RenderScrollback(
        IReadOnlyList<IScrollbackItem> items,
        ScrollbackRenderContext context)
    {
        var output = new List<string>();
        foreach (var item in items)
        {
            var rendered = item.Render(context);
            if (item.StartsLayout && rendered.Count > 0 && NeedsLeadingGap(item.Layout))
            {
                output.Add(string.Empty);
                _committedGap = true;
            }

            output.AddRange(rendered);
            if (rendered.Count > 0)
            {
                _committed = true;
                _committedGap = false;
                _lastLayout = item.Layout;
            }

            if (item.EndsLayout
                && item.Layout is ScrollbackLayout.User or ScrollbackLayout.Assistant or ScrollbackLayout.Block
                && !_committedGap)
            {
                output.Add(string.Empty);
                _committed = true;
                _committedGap = true;
                _lastLayout = item.Layout;
            }
        }

        return output;
    }

    private bool NeedsLeadingGap(ScrollbackLayout layout) => layout switch
    {
        ScrollbackLayout.User or ScrollbackLayout.Assistant => !_committedGap,
        ScrollbackLayout.Block => _committed && !_committedGap,
        _ => _committed && !_committedGap && _lastLayout == ScrollbackLayout.Block,
    };

    private TerminalFrame Render(IReadOnlyList<ILiveBufferItem> items, int width)
    {
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
                lines.Add(new TerminalLine(text, line.Style, line.StyleSpans));
            }
        }

        return new TerminalFrame(
            lines,
            caret ?? throw new InvalidOperationException("The live buffer has no caret."));
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
            var line = frame.Lines[row];
            _surface.Write(row, 0, line.Text, line.Style);
            foreach (var span in line.StyleSpans)
            {
                _surface.ApplyStyle(row, span.StartCell, span.Length, span.Style);
            }
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

    private async Task ReplaceFrame(TerminalFrame frame, int width, CancellationToken cancellationToken)
    {
        var previous = _frame ?? throw new InvalidOperationException("The live buffer has no previous frame.");
        var changed = ChangedRows(previous, frame, width != _renderedWidth);
        if (changed.Count == 0 && previous.Lines.Count == frame.Lines.Count && previous.Caret == frame.Caret)
        {
            return;
        }

        var previousHeight = _renderedHeight;
        var rowsBelowCaret = previousHeight - _caretRow - 1;
        await output.WriteAsync($"\u001b[?25l{DisableAutowrap}".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        if (rowsBelowCaret > 0)
        {
            await output.WriteAsync($"\u001b[{rowsBelowCaret}B".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        await output.WriteAsync("\r".AsMemory(), cancellationToken).ConfigureAwait(false);
        var rowsToReserve = Math.Max(0, frame.Lines.Count - previousHeight);
        for (var row = 0; row < rowsToReserve; row++)
        {
            await output.WriteAsync("\r\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        var rowsAbove = Math.Max(previousHeight, frame.Lines.Count) - 1;
        if (rowsAbove > 0)
        {
            await output.WriteAsync($"\u001b[{rowsAbove}A\r".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        _surface.Resize(width, frame.Lines.Count);
        _surface.Clear();
        for (var row = 0; row < frame.Lines.Count; row++)
        {
            var line = frame.Lines[row];
            _surface.Write(row, 0, line.Text, line.Style);
            foreach (var span in line.StyleSpans)
            {
                _surface.ApplyStyle(row, span.StartCell, span.Length, span.Style);
            }
        }

        var currentRow = 0;
        foreach (var row in changed)
        {
            await MoveToRow(currentRow, row, cancellationToken).ConfigureAwait(false);
            currentRow = row;
            await output.WriteAsync("\u001b[2K".AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(_surface.RenderRow(row, palette.LiveBackground).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        var removedRows = previousHeight - frame.Lines.Count;
        if (removedRows > 0)
        {
            await MoveToRow(currentRow, frame.Lines.Count - 1, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync($"\u001b[B\r\u001b[{removedRows}M".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            currentRow = frame.Lines.Count;
        }

        await MoveToRow(currentRow, frame.Caret.Row, cancellationToken).ConfigureAwait(false);
        if (frame.Caret.Cells > 0)
        {
            await output.WriteAsync($"\u001b[{frame.Caret.Cells}C".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        await output.WriteAsync($"{EnableAutowrap}\u001b[?25h".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        _renderedHeight = frame.Lines.Count;
        _caretRow = frame.Caret.Row;
    }

    private async Task MoveToRow(int currentRow, int targetRow, CancellationToken cancellationToken)
    {
        var distance = targetRow - currentRow;
        if (distance > 0)
        {
            await output.WriteAsync($"\u001b[{distance}B".AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        else if (distance < 0)
        {
            await output.WriteAsync($"\u001b[{-distance}A".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await output.WriteAsync("\r".AsMemory(), cancellationToken).ConfigureAwait(false);
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
        _renderedWidth = 0;
    }
}
