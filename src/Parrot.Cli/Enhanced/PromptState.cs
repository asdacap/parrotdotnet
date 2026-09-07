namespace Parrot.Cli.Enhanced;

internal readonly record struct PromptState(string Prefix, string Text, int Cursor)
{
    public PromptState Sanitize()
    {
        var cleanPrefix = TerminalText.Sanitize(Prefix).Replace("\n", string.Empty, StringComparison.Ordinal);
        var cleanText = TerminalText.Sanitize(Text);
        var runeCount = cleanText.EnumerateRunes().Count();
        return new PromptState(cleanPrefix, cleanText, Math.Clamp(Cursor, 0, runeCount));
    }
}
