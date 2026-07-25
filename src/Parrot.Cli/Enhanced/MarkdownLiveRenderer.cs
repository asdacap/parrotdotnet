using System.Text;

namespace Parrot.Cli.Enhanced;

internal sealed class MarkdownLiveRenderer(TextWriter output, Func<int> columns, bool color)
{
    private const int DefaultColumns = 80;
    private const int MaximumPreviewRows = 10;
    private const string HideCursor = "\u001b[?25l";
    private const string ShowCursor = "\u001b[?25h";
    private const string EraseLine = "\u001b[2K";

    private readonly StringBuilder _pending = new();
    private List<string> _liveRows = [];
    private string _id = string.Empty;
    private string _prefix = string.Empty;
    private bool _started;

    public async Task Append(LiveTerminalStreamMessage fragment, CancellationToken cancellationToken)
    {
        var id = TerminalText.Sanitize(fragment.Id);
        var prefix = TerminalText.Sanitize(fragment.Prefix);
        if (id.Length == 0)
        {
            throw new ArgumentException("The stream message ID is empty.", nameof(fragment));
        }

        if (_id.Length > 0 && !string.Equals(_id, id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Another assistant message is still streaming.");
        }

        if (_id.Length > 0 && !string.Equals(_prefix, prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The stream message prefix changed.");
        }

        var delta = TerminalText.Sanitize(fragment.Text);
        var candidate = _pending.ToString() + delta;
        var boundary = PromotableBoundary(candidate);
        var promotedSource = candidate[..boundary];
        var pendingSource = candidate[boundary..];
        var width = Columns();
        var renderPrefix = _started ? new string(' ', TerminalText.Width(prefix)) : prefix;
        var promoted = promotedSource.Length == 0
            ? []
            : MarkdownRenderer.Render(renderPrefix, promotedSource, width, color).ToList();
        var previewPrefix = _started || promoted.Count > 0
            ? new string(' ', TerminalText.Width(prefix))
            : prefix;
        var preview = pendingSource.Length == 0
            ? []
            : MarkdownRenderer.Render(previewPrefix, pendingSource, width, color)
                .TakeLast(MaximumPreviewRows)
                .ToList();
        var rendered = Promote(_liveRows, promoted, preview);
        await output.WriteAsync(rendered.AsMemory(), cancellationToken).ConfigureAwait(false);
        _id = id;
        _prefix = prefix;
        _ = _pending.Clear().Append(pendingSource);
        _started |= promoted.Count > 0;
        _liveRows = preview;
    }

    public async Task Commit(CancellationToken cancellationToken)
    {
        var prefix = _started ? new string(' ', TerminalText.Width(_prefix)) : _prefix;
        var rows = _pending.Length == 0
            ? []
            : MarkdownRenderer.Render(prefix, _pending.ToString(), Columns(), color);
        var rendered = new StringBuilder(Redraw(_liveRows, []));
        foreach (var row in rows)
        {
            _ = rendered.Append(row).Append('\n');
        }

        await output.WriteAsync(rendered.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        Reset();
    }

    public async Task Clear(CancellationToken cancellationToken)
    {
        var rendered = Redraw(_liveRows, []);
        await output.WriteAsync(rendered.AsMemory(), cancellationToken).ConfigureAwait(false);
        Reset();
    }

    private static string Promote(List<string> oldRows, List<string> promoted, List<string> liveRows)
    {
        var rendered = new StringBuilder(Redraw(oldRows, []));
        foreach (var row in promoted)
        {
            _ = rendered.Append(row).Append('\n');
        }

        return rendered.Append(Redraw([], liveRows)).ToString();
    }

    private static int PromotableBoundary(string source)
    {
        var safe = 0;
        var position = 0;
        var fenceMarker = '\0';
        var fenceLength = 0;
        var pendingTable = false;
        while (position < source.Length)
        {
            var newline = source.IndexOf('\n', position);
            if (newline < 0)
            {
                break;
            }

            var line = source[position..newline].TrimEnd('\r');
            if (fenceMarker != '\0')
            {
                if (Fence(line, fenceMarker, fenceLength, true, out _, out _))
                {
                    fenceMarker = '\0';
                    fenceLength = 0;
                    safe = newline + 1;
                }

                position = newline + 1;
                continue;
            }

            if (Fence(line, '\0', 0, false, out fenceMarker, out fenceLength))
            {
                position = newline + 1;
                continue;
            }

            if (line.Contains('|', StringComparison.Ordinal))
            {
                pendingTable = true;
                position = newline + 1;
                continue;
            }

            if (pendingTable)
            {
                safe = position;
                pendingTable = false;
                if (safe > 0)
                {
                    return safe;
                }
            }

            safe = newline + 1;
            position = newline + 1;
        }

        return safe;
    }

    private static bool Fence(
        string line,
        char expected,
        int minimum,
        bool closing,
        out char marker,
        out int length)
    {
        var trimmed = line.TrimStart(' ');
        marker = '\0';
        length = 0;
        if (line.Length - trimmed.Length > 3 || trimmed.Length < 3 || trimmed[0] is not ('`' or '~'))
        {
            return false;
        }

        marker = trimmed[0];
        while (length < trimmed.Length && trimmed[length] == marker)
        {
            length++;
        }

        if (length < 3 || (closing && (marker != expected || length < minimum || trimmed[length..].Trim().Length > 0)))
        {
            marker = '\0';
            length = 0;
            return false;
        }

        return true;
    }

    private static string Redraw(List<string> oldRows, List<string> newRows)
    {
        if (oldRows.Count == 0 && newRows.Count == 0)
        {
            return string.Empty;
        }

        var rendered = new StringBuilder(HideCursor);
        if (oldRows.Count > 0)
        {
            _ = rendered.Append('\r');
            if (oldRows.Count > 1)
            {
                _ = rendered.Append("\u001b[").Append(oldRows.Count - 1).Append('A');
            }
        }

        var count = Math.Max(oldRows.Count, newRows.Count);
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
            if (count > 1)
            {
                _ = rendered.Append("\u001b[").Append(count - 1).Append('A');
            }
        }

        return rendered.Append(ShowCursor).ToString();
    }

    private int Columns()
    {
        try
        {
            return Math.Max(1, columns());
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

    private void Reset()
    {
        _id = string.Empty;
        _prefix = string.Empty;
        _ = _pending.Clear();
        _liveRows = [];
        _started = false;
    }
}
