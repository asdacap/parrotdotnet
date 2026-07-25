using System.Text;

namespace Parrot.Cli.Enhanced;

internal sealed class TerminalKeyDecoder
{
    private const int MaximumPasteBytes = 64 * 1024;
    private const string PasteStart = "\u001b[200~";
    private const string PasteEnd = "\u001b[201~";
    private readonly List<byte> _bytes = [];
    private readonly List<byte> _paste = [];
    private bool _pasting;

    public IReadOnlyList<TerminalKey> Feed(ReadOnlySpan<byte> bytes)
    {
        _bytes.AddRange(bytes.ToArray());
        return DecodeAvailable();
    }

    public IReadOnlyList<TerminalKey> Flush()
    {
        if (_bytes.Count == 1 && _bytes[0] == 0x1b)
        {
            _bytes.Clear();
            return [new TerminalKey(TerminalKeyKind.Escape)];
        }

        return DecodeAvailable();
    }

    private static TerminalKey? Control(byte value) => value switch
    {
        0x01 => new(TerminalKeyKind.Home),
        0x03 => new(TerminalKeyKind.Interrupt),
        0x04 => new(TerminalKeyKind.EndOfFile),
        0x05 => new(TerminalKeyKind.End),
        0x08 or 0x7f => new(TerminalKeyKind.Backspace),
        0x09 => new(TerminalKeyKind.Complete),
        0x0a or 0x0d => new(TerminalKeyKind.Submit),
        0x0b => new(TerminalKeyKind.KillLine),
        0x18 or 0x1e => new(TerminalKeyKind.Mode),
        _ => null,
    };

    private List<TerminalKey> DecodeAvailable()
    {
        var keys = new List<TerminalKey>();
        while (Decode(keys))
        {
        }

        return keys;
    }

    private bool Decode(List<TerminalKey> keys)
    {
        if (_bytes.Count == 0)
        {
            return false;
        }

        if (_pasting)
        {
            var marker = Encoding.ASCII.GetBytes(PasteEnd);
            var end = IndexOf(marker);
            var available = end < 0 ? Math.Max(0, _bytes.Count - marker.Length + 1) : end;
            var accepted = Math.Min(available, MaximumPasteBytes - _paste.Count);
            _paste.AddRange(_bytes.Take(accepted));
            _bytes.RemoveRange(0, available);
            if (end < 0)
            {
                return false;
            }

            _bytes.RemoveRange(0, marker.Length);
            var value = Encoding.UTF8.GetString([.. _paste]).Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            _paste.Clear();
            _pasting = false;
            keys.Add(new TerminalKey(TerminalKeyKind.Paste, TerminalText.Sanitize(value)));
            return true;
        }

        if (StartsWith(PasteStart))
        {
            _bytes.RemoveRange(0, PasteStart.Length);
            _pasting = true;
            return true;
        }

        var sequence = EscapeSequence();
        if (sequence is not null)
        {
            keys.Add(sequence.Value);
            return true;
        }

        if (_bytes[0] == 0x1b)
        {
            return false;
        }

        var first = _bytes[0];
        _bytes.RemoveAt(0);
        var control = Control(first);
        if (control is not null)
        {
            keys.Add(control.Value);
            return true;
        }

        if (first < 0x20)
        {
            return true;
        }

        var length = first < 0x80 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
        if (_bytes.Count < length - 1)
        {
            _bytes.Insert(0, first);
            return false;
        }

        var runeBytes = new byte[length];
        runeBytes[0] = first;
        for (var index = 1; index < length; index++)
        {
            runeBytes[index] = _bytes[0];
            _bytes.RemoveAt(0);
        }

        var text = Encoding.UTF8.GetString(runeBytes);
        if (!text.Contains('\ufffd', StringComparison.Ordinal))
        {
            keys.Add(new TerminalKey(TerminalKeyKind.Character, text));
        }

        return true;
    }

    private TerminalKey? EscapeSequence()
    {
        if (_bytes[0] != 0x1b)
        {
            return null;
        }

        var sequences = new Dictionary<string, TerminalKeyKind>(StringComparer.Ordinal)
        {
            ["\u001b[A"] = TerminalKeyKind.Up,
            ["\u001b[B"] = TerminalKeyKind.Down,
            ["\u001b[C"] = TerminalKeyKind.Right,
            ["\u001b[D"] = TerminalKeyKind.Left,
            ["\u001b[H"] = TerminalKeyKind.Home,
            ["\u001b[F"] = TerminalKeyKind.End,
            ["\u001b[3~"] = TerminalKeyKind.Delete,
            ["\u001b[Z"] = TerminalKeyKind.Mode,
            ["\u001bOH"] = TerminalKeyKind.Home,
            ["\u001bOF"] = TerminalKeyKind.End,
        };
        foreach (var pair in sequences)
        {
            if (StartsWith(pair.Key))
            {
                _bytes.RemoveRange(0, pair.Key.Length);
                return new TerminalKey(pair.Value);
            }
        }

        if (_bytes.Count == 1)
        {
            return null;
        }

        if (_bytes.Count >= 2 && _bytes[1] is not (byte)'[' and not (byte)'O')
        {
            _bytes.RemoveAt(0);
            return new TerminalKey(TerminalKeyKind.Escape);
        }

        var terminator = _bytes.FindIndex(2, value => value is >= 0x40 and <= 0x7e);
        if (terminator >= 0)
        {
            _bytes.RemoveRange(0, terminator + 1);
            return new TerminalKey(TerminalKeyKind.Escape);
        }

        return null;
    }

    private bool StartsWith(string value) => StartsWith(Encoding.ASCII.GetBytes(value));

    private bool StartsWith(byte[] value) =>
        _bytes.Count >= value.Length && !_bytes.Take(value.Length).Where((item, index) => item != value[index]).Any();

    private int IndexOf(byte[] value)
    {
        for (var index = 0; index <= _bytes.Count - value.Length; index++)
        {
            if (!_bytes.Skip(index).Take(value.Length).Where((item, offset) => item != value[offset]).Any())
            {
                return index;
            }
        }

        return -1;
    }
}
