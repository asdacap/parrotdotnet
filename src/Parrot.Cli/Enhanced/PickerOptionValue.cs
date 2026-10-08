namespace Parrot.Cli.Enhanced;

internal readonly record struct PickerOptionValue(string Label, string Description, bool Selected) : ILiveBufferItem
{
    public MultiLine Render(LiveBufferRenderContext context)
    {
        var marker = Selected ? "> " : "  ";
        var label = TerminalText.SanitizeLine(Label);
        var description = TerminalText.SanitizeLine(Description);
        var text = description.Length == 0 ? marker + label : $"{marker}{label} — {description}";
        var clipped = string.Concat(text.EnumerateRunes().Take(Math.Max(1, context.Columns)));
        var style = Selected ? context.Palette.Selection : context.Palette.LiveSurface;
        return new MultiLine(
            [.. TerminalText.LayoutWords(clipped, context.Columns).Take(1).Select(value => new TerminalLine(value, style))],
            null,
            LiveBufferRetention.Fixed);
    }
}
