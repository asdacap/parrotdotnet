using System.Globalization;
using System.Text;

namespace Parrot.Cli.Enhanced;

// Owns the one mutable row at the bottom of normal terminal scrollback. The
// input is cumulative: once a physical row is followed by another, that stable
// row is promoted and only the unfinished suffix remains redrawable.
internal sealed class LiveTerminalRenderer(TextWriter output, Func<int> columns)
{
    private const int DefaultColumns = 80;
    private const string HideCursor = "\u001b[?25l";
    private const string ShowCursor = "\u001b[?25h";
    private const string EraseLine = "\u001b[2K";

    private readonly StringBuilder _text = new();
    private string _id = string.Empty;
    private string _prefix = string.Empty;
    private List<Rune> _pending = [];
    private bool _started;
    private List<string> _liveRows = [];

    public Task Update(LiveTerminalStreamMessage message, CancellationToken cancellationToken)
    {
        var clean = Sanitize(message);
        return AdvanceAndWrite(clean.Id, clean.Prefix, CumulativeDelta(clean.Text), false, cancellationToken);
    }

    public Task Append(LiveTerminalStreamMessage fragment, CancellationToken cancellationToken)
    {
        var clean = Sanitize(fragment);
        return AdvanceAndWrite(clean.Id, clean.Prefix, clean.Text, false, cancellationToken);
    }

    public Task Commit(LiveTerminalStreamMessage message, CancellationToken cancellationToken)
    {
        var clean = Sanitize(message);
        return AdvanceAndWrite(clean.Id, clean.Prefix, CumulativeDelta(clean.Text), true, cancellationToken);
    }

    public Task Commit(CancellationToken cancellationToken) =>
        AdvanceAndWrite(_id, _prefix, string.Empty, true, cancellationToken);

    public async Task Clear(CancellationToken cancellationToken)
    {
        var rendered = Redraw(_liveRows, [], Columns());
        await output.WriteAsync(rendered.AsMemory(), cancellationToken).ConfigureAwait(false);
        _liveRows = [];
    }

    private static string RenderPromotion(
        List<string> oldRows,
        List<string> promoted,
        List<string> liveRows,
        int width)
    {
        if (promoted.Count == 0)
        {
            return Redraw(oldRows, liveRows, width);
        }

        var rendered = new StringBuilder(Redraw(oldRows, [], width));
        foreach (var row in promoted)
        {
            _ = rendered.Append(row).Append('\n');
        }

        return rendered.Append(Redraw([], liveRows, width)).ToString();
    }

    private static string Redraw(List<string> oldRows, List<string> newRows, int width)
    {
        var oldPhysical = PhysicalRows(oldRows, width);
        if (oldPhysical.Count == 0 && newRows.Count == 0)
        {
            return string.Empty;
        }

        var rendered = new StringBuilder(HideCursor);
        if (oldPhysical.Count > 0)
        {
            _ = rendered.Append('\r');
            MoveUp(rendered, oldPhysical.Count - 1);
        }

        var count = Math.Max(oldPhysical.Count, newRows.Count);
        for (var index = 0; index < count; index++)
        {
            _ = rendered.Append(EraseLine);
            if (index < newRows.Count)
            {
                _ = rendered.Append(newRows[index]);
            }

            if (index + 1 < count)
            {
                _ = rendered.Append("\r\n");
            }
        }

        if (count > 0 && newRows.Count == 0)
        {
            _ = rendered.Append('\r');
            MoveUp(rendered, count - 1);
        }

        return rendered.Append(ShowCursor).ToString();
    }

    private static LayoutResult Layout(string prefix, List<Rune> source, int width)
    {
        var indent = new string(' ', TerminalText.Width(prefix));
        if (indent.Length >= width)
        {
            indent = string.Empty;
        }

        var rows = new List<StringBuilder> { new(prefix) };
        var boundaries = new List<int> { 0 };
        var row = 0;
        var column = TerminalText.Width(prefix);

        for (var index = 0; index < source.Count; index++)
        {
            var raw = source[index];
            if (raw.Value == '\n')
            {
                boundaries[row] = index + 1;
                rows.Add(new StringBuilder(indent));
                boundaries.Add(index + 1);
                row++;
                column = indent.Length;
                continue;
            }

            var end = ClusterEnd(source, index);
            var clusterWidth = ClusterWidth(source, index, end);
            if (column > 0 && column + clusterWidth > width)
            {
                boundaries[row] = index;
                rows.Add(new StringBuilder(indent));
                boundaries.Add(index);
                row++;
                column = indent.Length;
            }

            for (var clusterIndex = index; clusterIndex < end; clusterIndex++)
            {
                _ = rows[row].Append(source[clusterIndex]);
            }

            column += clusterWidth;
            boundaries[row] = end;
            index = end - 1;
        }

        return new LayoutResult([.. rows.Select(static value => value.ToString())], boundaries);
    }

    private static int ClusterEnd(List<Rune> source, int start)
    {
        var end = start + 1;
        if (source[start].Value is >= 0x1f1e6 and <= 0x1f1ff
            && end < source.Count
            && source[end].Value is >= 0x1f1e6 and <= 0x1f1ff)
        {
            return end + 1;
        }

        while (end < source.Count)
        {
            var category = Rune.GetUnicodeCategory(source[end]);
            if (category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark
                || source[end].Value is 0xfe0e or 0xfe0f
                || source[end].Value is >= 0x1f3fb and <= 0x1f3ff)
            {
                end++;
                continue;
            }

            if (source[end].Value == 0x200d)
            {
                end++;
                if (end < source.Count)
                {
                    end++;
                }

                continue;
            }

            break;
        }

        return end;
    }

    private static int ClusterWidth(List<Rune> source, int start, int end)
    {
        if (end - start == 2
            && source[start].Value is >= 0x1f1e6 and <= 0x1f1ff
            && source[start + 1].Value is >= 0x1f1e6 and <= 0x1f1ff)
        {
            return 2;
        }

        var width = 0;
        var emojiPresentation = false;
        for (var index = start; index < end; index++)
        {
            if (source[index].Value == 0x200d)
            {
                continue;
            }

            emojiPresentation |= source[index].Value == 0xfe0f;
            width = Math.Max(width, TerminalText.Width(source[index]));
        }

        return emojiPresentation ? Math.Max(2, width) : width;
    }

    private static List<string> PhysicalRows(List<string> rows, int width)
    {
        var physical = new List<string>();
        foreach (var value in rows)
        {
            physical.AddRange(Layout(string.Empty, [.. value.EnumerateRunes()], width).Rows);
        }

        return physical;
    }

    private static void MoveUp(StringBuilder rendered, int rows)
    {
        if (rows > 0)
        {
            _ = rendered.Append("\u001b[").Append(rows.ToString(CultureInfo.InvariantCulture)).Append('A');
        }
    }

    private static LiveTerminalStreamMessage Sanitize(LiveTerminalStreamMessage message)
    {
        var clean = new LiveTerminalStreamMessage(
            TerminalText.Sanitize(message.Id),
            TerminalText.Sanitize(message.Prefix),
            TerminalText.Sanitize(message.Text));
        if (clean.Id.Length == 0)
        {
            throw new ArgumentException("The stream message ID is empty.", nameof(message));
        }

        return clean;
    }

    private async Task AdvanceAndWrite(
        string id,
        string prefix,
        string delta,
        bool complete,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(id, prefix);
        var width = Columns();
        var advanced = Advance(id, prefix, delta, complete, width);
        var liveRows = complete ? [] : advanced.LiveRows;
        var rendered = RenderPromotion(_liveRows, advanced.Promoted, liveRows, width);
        await output.WriteAsync(rendered.AsMemory(), cancellationToken).ConfigureAwait(false);

        if (complete)
        {
            Reset();
        }
        else
        {
            Accept(advanced);
        }
    }

    private AdvancedStream Advance(string id, string prefix, string delta, bool complete, int width)
    {
        var pending = new List<Rune>(_pending);
        foreach (var rune in delta.EnumerateRunes())
        {
            pending.Add(rune);
        }

        var layoutPrefix = _started ? new string(' ', TerminalText.Width(prefix)) : prefix;
        var layout = Layout(layoutPrefix, pending, width);
        var commitCount = complete ? layout.Rows.Count : layout.Rows.Count - 1;

        if (complete && commitCount > 0 && EndsWithNewline(delta))
        {
            commitCount--;
        }

        commitCount = Math.Max(0, commitCount);
        var promoted = layout.Rows.Take(commitCount).ToList();
        var started = _started;

        if (commitCount > 0)
        {
            pending.RemoveRange(0, layout.Boundaries[commitCount - 1]);
            started = true;
        }

        var liveRows = complete ? [] : layout.Rows.Skip(commitCount).ToList();
        return new AdvancedStream(id, prefix, delta, pending, started, promoted, liveRows);
    }

    private string CumulativeDelta(string text)
    {
        if (text.Length < _text.Length)
        {
            throw new InvalidOperationException("The streamed assistant text changed.");
        }

        for (var index = 0; index < _text.Length; index++)
        {
            if (_text[index] != text[index])
            {
                throw new InvalidOperationException("The streamed assistant text changed.");
            }
        }

        return text[_text.Length..];
    }

    private bool EndsWithNewline(string delta) =>
        delta.Length > 0 ? delta[^1] == '\n' : _text.Length > 0 && _text[^1] == '\n';

    private void ValidateIdentity(string id, string prefix)
    {
        if (_id.Length > 0 && !string.Equals(_id, id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Another assistant message is still streaming.");
        }

        if (_id.Length > 0 && !string.Equals(_prefix, prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The stream message prefix changed.");
        }
    }

    private int Columns()
    {
        try
        {
            var current = columns();
            return current > 0 ? current : DefaultColumns;
        }
        catch (IOException)
        {
            return DefaultColumns;
        }
        catch (InvalidOperationException)
        {
            return DefaultColumns;
        }
        catch (PlatformNotSupportedException)
        {
            return DefaultColumns;
        }
    }

    private void Accept(AdvancedStream advanced)
    {
        _id = advanced.Id;
        _prefix = advanced.Prefix;
        _ = _text.Append(advanced.Delta);
        _pending = advanced.Pending;
        _started = advanced.Started;
        _liveRows = advanced.LiveRows;
    }

    private void Reset()
    {
        _id = string.Empty;
        _prefix = string.Empty;
        _ = _text.Clear();
        _pending = [];
        _started = false;
        _liveRows = [];
    }

    private sealed record LayoutResult(List<string> Rows, List<int> Boundaries);

    private sealed record AdvancedStream(
        string Id,
        string Prefix,
        string Delta,
        List<Rune> Pending,
        bool Started,
        List<string> Promoted,
        List<string> LiveRows);
}
