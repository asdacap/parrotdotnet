namespace Parrot.Cli.Enhanced;

internal readonly record struct PromptValue(string Prefix, string Text, int Cursor)
{
    public PromptValue Sanitize()
    {
        var cleanPrefix = TerminalText.Sanitize(Prefix).Replace("\n", string.Empty, StringComparison.Ordinal);
        var cleanText = TerminalText.Sanitize(Text);
        var runeCount = cleanText.EnumerateRunes().Count();
        return new PromptValue(cleanPrefix, cleanText, Math.Clamp(Cursor, 0, runeCount));
    }

    public int CursorCells()
    {
        var clean = Sanitize();
        var width = TerminalText.Width(clean.Prefix);
        var index = 0;
        foreach (var rune in clean.Text.EnumerateRunes())
        {
            if (index++ >= clean.Cursor)
            {
                break;
            }

            width = rune.Value == '\n' ? 0 : width + TerminalText.Width(rune);
        }

        return width;
    }
}
