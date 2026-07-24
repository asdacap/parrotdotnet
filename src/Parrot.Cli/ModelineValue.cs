namespace Parrot.Cli;

internal readonly record struct ModelineValue(string Mode, string Activity, string Model)
{
    public string Render(int width)
    {
        var left = TerminalText.Sanitize(string.Join(" · ", new[] { Mode, Activity }.Where(value => value.Length > 0)));
        var right = TerminalText.Sanitize(Model);
        if (right.Length == 0)
        {
            return Clip(left, width);
        }

        if (TerminalText.Width(left) + TerminalText.Width(right) + 1 > width)
        {
            return Clip(right, width);
        }

        return left + new string(' ', width - TerminalText.Width(left) - TerminalText.Width(right)) + right;
    }

    private static string Clip(string value, int width)
    {
        var rendered = new System.Text.StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeWidth = TerminalText.Width(rune);
            if (used + runeWidth > width)
            {
                break;
            }

            _ = rendered.Append(rune);
            used += runeWidth;
        }

        return rendered.ToString();
    }
}
