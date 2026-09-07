using System.Text;

namespace Parrot.Cli.Enhanced;

internal sealed class IncrementalEditor(string prefix, int maximumRunes)
{
    private readonly List<Rune> _text = [];
    private int _cursor;

    public bool IsEmpty => _text.Count == 0;

    public PromptState Prompt => new(prefix, string.Concat(_text), _cursor);

    public string? Apply(TerminalKey key)
    {
        switch (key.Kind)
        {
            case TerminalKeyKind.Character:
            case TerminalKeyKind.Paste:
                Insert(key.Text);
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
                break;
            case TerminalKeyKind.Delete when _cursor < _text.Count:
                _text.RemoveAt(_cursor);
                break;
            case TerminalKeyKind.KillLine:
                KillLine();
                break;
            case TerminalKeyKind.Newline:
                Insert("\n");
                break;
            case TerminalKeyKind.EndOfFile when _text.Count > 0:
                if (_cursor < _text.Count)
                {
                    _text.RemoveAt(_cursor);
                }

                break;
            case TerminalKeyKind.Submit:
                var submitted = string.Concat(_text).Trim();
                _text.Clear();
                _cursor = 0;
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

        Clear();
        Insert(value);
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
    }

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
