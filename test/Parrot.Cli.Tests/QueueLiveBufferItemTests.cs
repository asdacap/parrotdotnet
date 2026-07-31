using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class QueueLiveBufferItemTests
{
    [Test]
    public async Task Render_shows_name_description_and_plural_count()
    {
        var rendered = new QueueLiveBufferItem("deploy", "release tasks", 3)
            .Render(new LiveBufferRenderContext(80, new TerminalPalette(false)));

        _ = await Assert.That(rendered.Lines).Count().IsEqualTo(1);
        _ = await Assert.That(rendered.Lines[0].Text).IsEqualTo("  queue: deploy · 3 items — release tasks");
        _ = await Assert.That(rendered.Lines[0].Style).IsEqualTo(new TerminalPalette(false).LiveMuted);
        _ = await Assert.That(rendered.Retention).IsEqualTo(LiveBufferRetention.Fixed);
    }

    [Test]
    public async Task Render_uses_name_for_blank_description_and_singular_count()
    {
        var rendered = new QueueLiveBufferItem(" release\n tasks ", " \n ", 1)
            .Render(new LiveBufferRenderContext(80, new TerminalPalette(false)));

        _ = await Assert.That(rendered.Lines[0].Text).IsEqualTo("  queue: release  tasks · 1 item");
    }

    [Test]
    public async Task Render_sanitizes_and_clips_description_without_losing_count()
    {
        var rendered = new QueueLiveBufferItem("name", "a\u001b[2J\n界bc", 12)
            .Render(new LiveBufferRenderContext(31, new TerminalPalette(false)));

        _ = await Assert.That(rendered.Lines).Count().IsEqualTo(1);
        _ = await Assert.That(rendered.Lines[0].Text).IsEqualTo("  queue: name · 12 items — a[2J");
        _ = await Assert.That(TerminalText.Width(rendered.Lines[0].Text)).IsLessThanOrEqualTo(31);
    }
}
