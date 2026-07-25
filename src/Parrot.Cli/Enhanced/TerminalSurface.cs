using System.Text;

namespace Parrot.Cli.Enhanced;

internal sealed class TerminalSurface
{
    private string[] _characters;
    private TerminalStyle[] _styles;

    public TerminalSurface(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _characters = new string[Height * Width];
        _styles = new TerminalStyle[Height * Width];
        Clear();
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height)
        {
            return;
        }

        Width = width;
        Height = height;
        _characters = new string[Height * Width];
        _styles = new TerminalStyle[Height * Width];
        Clear();
    }

    public void Clear()
    {
        for (var row = 0; row < Height; row++)
        {
            for (var column = 0; column < Width; column++)
            {
                _characters[Index(row, column)] = " ";
                _styles[Index(row, column)] = default;
            }
        }
    }

    public void Write(int row, int column, string value, TerminalStyle style)
    {
        if (row < 0 || row >= Height || column >= Width)
        {
            return;
        }

        var currentColumn = Math.Max(0, column);
        foreach (var rune in value.EnumerateRunes())
        {
            var runeWidth = TerminalText.Width(rune);
            if (runeWidth == 0)
            {
                if (currentColumn > 0)
                {
                    _characters[Index(row, currentColumn - 1)] += rune.ToString();
                }

                continue;
            }

            if (currentColumn + runeWidth > Width)
            {
                break;
            }

            _characters[Index(row, currentColumn)] = rune.ToString();
            _styles[Index(row, currentColumn)] = style;
            for (var occupied = 1; occupied < runeWidth; occupied++)
            {
                _characters[Index(row, currentColumn + occupied)] = string.Empty;
                _styles[Index(row, currentColumn + occupied)] = style;
            }

            currentColumn += runeWidth;
        }
    }

    public string Text(int row)
    {
        var text = new StringBuilder();
        for (var column = 0; column < Width; column++)
        {
            _ = text.Append(_characters[Index(row, column)]);
        }

        return text.ToString();
    }

    public string RenderRow(int row, TerminalStyle background)
    {
        var rendered = new StringBuilder();
        var active = default(TerminalStyle);
        if (!string.IsNullOrEmpty(background.Start))
        {
            _ = rendered.Append(background.Start);
            active = background;
        }

        for (var column = 0; column < Width; column++)
        {
            var cellStyle = _styles[Index(row, column)];
            var style = string.IsNullOrEmpty(cellStyle.Start) ? background : cellStyle;
            if (!string.Equals(active.Start, style.Start, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(active.Start))
                {
                    _ = rendered.Append(TerminalStyle.Reset);
                }

                if (!string.IsNullOrEmpty(style.Start))
                {
                    _ = rendered.Append(style.Start);
                }

                active = style;
            }

            _ = rendered.Append(_characters[Index(row, column)]);
        }

        if (!string.IsNullOrEmpty(active.Start))
        {
            _ = rendered.Append(TerminalStyle.Reset);
        }

        return rendered.ToString();
    }

    private int Index(int row, int column) => (row * Width) + column;
}
