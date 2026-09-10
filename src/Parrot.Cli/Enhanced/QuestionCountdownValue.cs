using System.Globalization;

namespace Parrot.Cli.Enhanced;

internal readonly record struct QuestionCountdownValue(long RemainingSeconds) : ILiveBufferItem
{
    public MultiLine Render(LiveBufferRenderContext context) => new(
        [.. TerminalText.Layout(
            string.Create(CultureInfo.InvariantCulture, $"Auto-return in {RemainingSeconds / 60}:{RemainingSeconds % 60:00}"),
            context.Columns).Select(value => new TerminalLine(value, context.Palette.LiveSurface))],
        null,
        LiveBufferRetention.Fixed);
}
