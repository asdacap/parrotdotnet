using System.Text;

namespace Parrot.Cli.Enhanced;

internal sealed class MarkdownLiveRenderer(Func<int> columns, bool color)
{
    private const int DefaultColumns = 80;

    private readonly StringBuilder _pending = new();
    private string _id = string.Empty;
    private string _prefix = string.Empty;
    private StreamingScrollbackSequenceValue _sequence = new();
    private bool _started;

    public MarkdownLiveUpdate Append(LiveTerminalStreamMessage fragment)
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
        _id = id;
        _prefix = prefix;
        _ = _pending.Clear().Append(pendingSource);
        _started |= promoted.Count > 0;
        return new MarkdownLiveUpdate(
            promoted.Count == 0 ? null : _sequence.Append(promoted),
            previewPrefix,
            pendingSource.Length == 0 ? [] : [pendingSource]);
    }

    public MarkdownLiveUpdate Commit()
    {
        var prefix = _started ? new string(' ', TerminalText.Width(_prefix)) : _prefix;
        var scrollback = _pending.Length == 0
            ? []
            : MarkdownRenderer.Render(prefix, _pending.ToString(), Columns(), color);
        var completed = _sequence.Complete(scrollback);
        Reset();
        return new MarkdownLiveUpdate(completed, string.Empty, []);
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
        _sequence = new StreamingScrollbackSequenceValue();
        _started = false;
    }
}
