using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedWaitToolPresenterTests
{
    private static readonly LiveBufferRenderContext LiveContext = new(512, new TerminalPalette(false));

    [Test]
    public async Task Wait_renders_the_incoming_activity_live_label()
    {
        var presenter = new WaitToolPresenter();
        var call = new ToolCallPresentation("main", "wait", "{\"duration_ms\":10000}");

        var rendered = presenter.PresentLive(call, 0).Render(LiveContext).Lines;

        _ = await Assert.That(rendered[0].Text).IsEqualTo("⠋ main: Wait for incoming activity");
    }

    [Test]
    public async Task Wait_is_live_only_and_modeline_eligible()
    {
        var presenter = new WaitToolPresenter();
        var call = new ToolCallPresentation("main", "wait", "{}");

        var live = (IToolPresentationValue)presenter.PresentLive(call, 0);

        _ = await Assert.That(live.Report.Metadata.LiveOnly).IsTrue();
        _ = await Assert.That(live.Report.Metadata.Modeline).IsTrue();
    }

    [Test]
    public async Task Wait_omits_terminal_output()
    {
        var presenter = new WaitToolPresenter();
        var call = new ToolCallPresentation("main", "wait", "{}");
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "timed out", string.Empty);

        _ = await Assert.That(presenter.PresentTerminal(call, terminal)).IsNull();
    }
}
