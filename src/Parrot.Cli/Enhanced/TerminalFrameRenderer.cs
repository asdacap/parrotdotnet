using System.Text;
using System.Threading.Channels;

namespace Parrot.Cli.Enhanced;

internal sealed class TerminalFrameRenderer(
    TextWriter output,
    Func<int> columns,
    Func<int> rows,
    TerminalPalette palette,
    int maxLiveRows,
    int maxInputRows,
    bool inlineDiff)
{
    internal const int DefaultInputRows = 12;

    private const string DisableAutowrap = "\u001b[?7l";
    private const string EnableAutowrap = "\u001b[?7h";

    private readonly Channel<bool> _drawing = CreateDrawingGate();
    private readonly StringBuilder _pendingOutput = new();
    private readonly TerminalSurface _surface = new(1, 1);
    private IScrollbackItem? _activeScrollback;
    private int _caretRow;
    private bool _committed;
    private bool _committedGap;
    private TerminalFrame? _frame;
    private ScrollbackLayout _lastLayout;
    private object? _lastPacking;
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
                DrawFrame(frame, width, 1);
            }
            else
            {
                ReplaceFrame(frame, width);
            }

            if (_pendingOutput.Length > 0)
            {
                await WritePendingOutput().ConfigureAwait(false);
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
            ClearFrame();
            _frame = null;
            _renderedWidth = 0;
            await WritePendingOutput().ConfigureAwait(false);
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
            ClearFrame();
            foreach (var line in lines)
            {
                _ = _pendingOutput.Append(line).Append("\r\n");
            }

            DrawFrame(frame, width, availableRows);
            await WritePendingOutput().ConfigureAwait(false);
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
            var rendered = item.Render(context with { PreviousPacking = _lastPacking });
            if (item.StartsLayout && rendered.Count > 0 && !item.Packs(_lastPacking) && NeedsLeadingGap(item.Layout))
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
                _lastPacking = item.PackingIdentity;
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

        var frameCaret = caret ?? throw new InvalidOperationException("The live buffer has no caret.");
        var height = Math.Max(1, rows());
        var top = Math.Clamp(lines.Count - height, 0, frameCaret.Row);
        return new TerminalFrame(
            lines.GetRange(top, Math.Min(height, lines.Count - top)),
            frameCaret with { Row = frameCaret.Row - top });
    }

    private void DrawFrame(TerminalFrame frame, int width, int availableRows)
    {
        _renderedHeight = frame.Lines.Count;
        _caretRow = frame.Caret.Row;
        _surface.Resize(width, _renderedHeight);
        _surface.Clear();
        _ = _pendingOutput.Append($"\u001b[?25l{DisableAutowrap}");
        var rowsToReserve = Math.Max(0, _renderedHeight - availableRows);
        for (var row = 0; row < rowsToReserve; row++)
        {
            _ = _pendingOutput.Append("\r\n");
        }

        if (rowsToReserve > 0)
        {
            _ = _pendingOutput.Append($"\u001b[{rowsToReserve}A\r");
        }

        for (var row = 0; row < _renderedHeight; row++)
        {
            _ = _pendingOutput.Append("\u001b[2K").Append(RenderSurfaceRow(row, frame.Lines[row]));
            if (row < _renderedHeight - 1)
            {
                _ = _pendingOutput.Append("\r\n");
            }
        }

        var lastRow = _renderedHeight - 1;
        if (lastRow > _caretRow)
        {
            _ = _pendingOutput.Append($"\u001b[{lastRow - _caretRow}A");
        }

        _ = _pendingOutput.Append('\r');
        if (frame.Caret.Cells > 0)
        {
            _ = _pendingOutput.Append($"\u001b[{frame.Caret.Cells}C");
        }

        _ = _pendingOutput.Append($"{EnableAutowrap}\u001b[?25h");
    }

    private void ReplaceFrame(TerminalFrame frame, int width)
    {
        var previous = _frame ?? throw new InvalidOperationException("The live buffer has no previous frame.");
        var changed = ChangedRows(previous, frame, width != _renderedWidth);
        if (changed.Count == 0 && previous.Lines.Count == frame.Lines.Count && previous.Caret == frame.Caret)
        {
            return;
        }

        var previousHeight = _renderedHeight;
        var rowsBelowCaret = previousHeight - _caretRow - 1;
        _ = _pendingOutput.Append($"\u001b[?25l{DisableAutowrap}");
        if (rowsBelowCaret > 0)
        {
            _ = _pendingOutput.Append($"\u001b[{rowsBelowCaret}B");
        }

        _ = _pendingOutput.Append('\r');
        var rowsToReserve = Math.Max(0, frame.Lines.Count - previousHeight);
        for (var row = 0; row < rowsToReserve; row++)
        {
            _ = _pendingOutput.Append("\r\n");
        }

        var rowsAbove = Math.Max(previousHeight, frame.Lines.Count) - 1;
        if (rowsAbove > 0)
        {
            _ = _pendingOutput.Append($"\u001b[{rowsAbove}A\r");
        }

        _surface.Resize(width, frame.Lines.Count);
        _surface.Clear();
        var currentRow = 0;
        foreach (var row in changed)
        {
            MoveToRow(currentRow, row);
            currentRow = row;
            _ = _pendingOutput.Append("\u001b[2K").Append(RenderSurfaceRow(row, frame.Lines[row]));
        }

        var removedRows = previousHeight - frame.Lines.Count;
        if (removedRows > 0)
        {
            MoveToRow(currentRow, frame.Lines.Count - 1);
            _ = _pendingOutput.Append($"\u001b[B\r\u001b[{removedRows}M");
            currentRow = frame.Lines.Count;
        }

        MoveToRow(currentRow, frame.Caret.Row);
        if (frame.Caret.Cells > 0)
        {
            _ = _pendingOutput.Append($"\u001b[{frame.Caret.Cells}C");
        }

        _ = _pendingOutput.Append($"{EnableAutowrap}\u001b[?25h");
        _renderedHeight = frame.Lines.Count;
        _caretRow = frame.Caret.Row;
    }

    private string RenderSurfaceRow(int row, TerminalLine line)
    {
        _surface.Write(row, 0, line.Text, line.Style);
        foreach (var span in line.StyleSpans)
        {
            _surface.ApplyStyle(row, span.StartCell, span.Length, span.Style);
        }

        return _surface.RenderRow(row, palette.LiveBackground);
    }

    private void MoveToRow(int currentRow, int targetRow)
    {
        var distance = targetRow - currentRow;
        if (distance > 0)
        {
            _ = _pendingOutput.Append($"\u001b[{distance}B");
        }
        else if (distance < 0)
        {
            _ = _pendingOutput.Append($"\u001b[{-distance}A");
        }

        _ = _pendingOutput.Append('\r');
    }

    private void ClearFrame()
    {
        if (_renderedHeight == 0)
        {
            return;
        }

        _ = _pendingOutput.Append($"\u001b[?25l{DisableAutowrap}");
        var rowsBelowCaret = _renderedHeight - _caretRow - 1;
        if (rowsBelowCaret > 0)
        {
            _ = _pendingOutput.Append($"\u001b[{rowsBelowCaret}B");
        }

        _ = _pendingOutput.Append('\r');
        if (_renderedHeight > 1)
        {
            _ = _pendingOutput.Append($"\u001b[{_renderedHeight - 1}A");
        }

        for (var row = 0; row < _renderedHeight; row++)
        {
            _ = _pendingOutput.Append("\u001b[2K");
            if (row < _renderedHeight - 1)
            {
                _ = _pendingOutput.Append("\r\n");
            }
        }

        if (_renderedHeight > 1)
        {
            _ = _pendingOutput.Append($"\u001b[{_renderedHeight - 1}A");
        }

        _ = _pendingOutput.Append($"\r{EnableAutowrap}\u001b[?25h");
        _renderedHeight = 0;
        _caretRow = 0;
        _renderedWidth = 0;
    }

    private async Task WritePendingOutput()
    {
        var pending = _pendingOutput.ToString();
        _ = _pendingOutput.Clear();
        if (pending.Length > 0)
        {
            await output.WriteAsync(pending.AsMemory(), CancellationToken.None).ConfigureAwait(false);
        }

        await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
