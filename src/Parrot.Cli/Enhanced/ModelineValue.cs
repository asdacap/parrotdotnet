namespace Parrot.Cli.Enhanced;

internal readonly record struct ModelineValue(string Mode, string Activity, string Model) : ILiveBufferItem
{
    public string Render(int width)
    {
        var columns = Math.Max(1, width - 1);
        if (columns < 3)
        {
            return new string('─', columns);
        }

        var left = TerminalText.Sanitize(Mode);
        left = left.Length == 0 ? string.Empty : "mode: " + left;
        var center = TerminalText.Sanitize(Activity);
        var right = TerminalText.Sanitize(Model);
        var labels = new[] { left, center }.Where(value => value.Length > 0).ToArray();
        var leftPart = labels.Length == 0 ? string.Empty : "─ " + string.Join(" ─ ", labels) + " ";
        if (labels.Length == 0 && right.Length == 0)
        {
            return new string('─', columns);
        }

        if (right.Length == 0)
        {
            if (TerminalText.Width(leftPart) >= columns)
            {
                return TerminalText.Clip(leftPart, columns - 1) + "─";
            }

            return leftPart + new string('─', columns - TerminalText.Width(leftPart));
        }

        var rightPart = " " + right + " ";
        if (TerminalText.Width(rightPart) >= columns)
        {
            return "─" + TerminalText.Clip(rightPart, columns - 1);
        }

        var leftWidth = Math.Min(TerminalText.Width(leftPart), columns - TerminalText.Width(rightPart));
        leftPart = TerminalText.Clip(leftPart, leftWidth);
        return leftPart + new string('─', columns - leftWidth - TerminalText.Width(rightPart)) + rightPart;
    }

    public MultiLine Render(LiveBufferRenderContext context) => new(
        [new TerminalLine(Render(context.Columns), context.Palette.Modeline)],
        null,
        LiveBufferRetention.Fixed);
}
