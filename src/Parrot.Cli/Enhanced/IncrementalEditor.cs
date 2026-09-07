using System.Text;

namespace Parrot.Cli.Enhanced;

internal sealed class IncrementalEditor(string prefix, int maximumRunes)
{
    private readonly List<Rune> _text = [];
    private readonly List<string> _history = [];
    private string _draft = string.Empty;
    private int _cursor;
    private int _historyIndex;

    public bool IsEmpty => _text.Count == 0;

    public bool IsRecalling => _historyIndex > 0;

    public PromptState Prompt => new(prefix, string.Concat(_text), _cursor);

    /// <summary>Remembers a submitted entry so an empty prompt can revisit it.</summary>
    public void Remember(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Trim().Length > 0)
        {
            _history.Add(entry);
        }
    }

    /// <summary>Captures the draft before recall and steps back to the previous entry.</summary>
    public void Recall()
    {
        if (_historyIndex < _history.Count)
        {
            if (_historyIndex == 0)
            {
                _draft = string.Concat(_text);
            }

            _historyIndex++;
            ReplaceText(_history[^_historyIndex]);
        }
    }

    /// <summary>Steps forward one entry, or restores the draft past the most recent one.</summary>
    public void Next()
    {
        if (_historyIndex == 0)
        {
            return;
        }

        _historyIndex--;
        ReplaceText(_historyIndex == 0 ? _draft : _history[^_historyIndex]);
    }

    /// <summary>Replaces the draft without touching the history of submitted entries.</summary>
    public void ReplaceText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        Clear();
        Insert(value);
    }

    public string? Apply(TerminalKey key)
    {
        switch (key.Kind)
        {
            case TerminalKeyKind.Character:
            case TerminalKeyKind.Paste:
                Insert(key.Text);
                EndRecall();
                break;
            case TerminalKeyKind.Left:
                _cursor = Math.Max(0, _cursor - 1);
                break;
            case TerminalKeyKind.Right:
                _cursor = Math.Min(_text.Count, _cursor + 1);
                break;
            case TerminalKeyKind.Home:
                _cursor = LineStart();
                break;
            case TerminalKeyKind.PromptStart:
                _cursor = 0;
                break;
            case TerminalKeyKind.End:
                _cursor = LineEnd();
                break;
            case TerminalKeyKind.Backspace when _cursor > 0:
                _text.RemoveAt(--_cursor);
                EndRecall();
                break;
            case TerminalKeyKind.Delete when _cursor < _text.Count:
                _text.RemoveAt(_cursor);
                EndRecall();
                break;
            case TerminalKeyKind.KillLine:
                KillLine();
                EndRecall();
                break;
            case TerminalKeyKind.Newline:
                Insert("\n");
                EndRecall();
                break;
            case TerminalKeyKind.EndOfFile when _text.Count > 0:
                if (_cursor < _text.Count)
                {
                    _text.RemoveAt(_cursor);
                }

                EndRecall();
                break;
            case TerminalKeyKind.Submit:
                var submitted = string.Concat(_text).Trim();
                _text.Clear();
                _cursor = 0;
                _historyIndex = 0;
                _draft = string.Empty;
                return submitted;
            default:
                break;
        }

        return null;
    }

    public void Clear()
    {
        _text.Clear();
        _cursor = 0;
    }

    public void Replace(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        ReplaceText(value);
        EndRecall();
    }

    public void ReplaceRange(int start, int length, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start > _text.Count || length > _text.Count - start)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _text.RemoveRange(start, length);
        _cursor = start;
        Insert(value);
        EndRecall();
    }

    private void EndRecall() => _historyIndex = 0;

    private void Insert(string value)
    {
        var runes = value.EnumerateRunes().Where(rune => rune.Value == '\n' || !Rune.IsControl(rune)).ToList();
        var accepted = Math.Min(runes.Count, maximumRunes - _text.Count);
        _text.InsertRange(_cursor, runes.Take(accepted));
        _cursor += accepted;
    }

    private int LineStart()
    {
        var index = _cursor;
        while (index > 0 && _text[index - 1].Value != '\n')
        {
            index--;
        }

        return index;
    }

    private int LineEnd()
    {
        var index = _cursor;
        while (index < _text.Count && _text[index].Value != '\n')
        {
            index++;
        }

        return index;
    }

    private void KillLine()
    {
        var end = LineEnd();
        if (end == _cursor && end < _text.Count)
        {
            end++;
        }

        _text.RemoveRange(_cursor, end - _cursor);
    }
}
