using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedWaitToolPresenterTests
{
    private static readonly LiveBufferRenderContext LiveContext = new(512, new TerminalPalette(false));

    [Test]
    [Arguments("{\"duration_ms\":130000}", "⠋ Wait for incoming activity (2m 10s left)", "⠙ Wait for incoming activity (2m 00s left)")]
    [Arguments("{}", "⠋ Wait for incoming activity (10s left)", "⠙ Wait for incoming activity (0s left)")]
    public async Task Wait_counts_down_the_remaining_duration(string argumentsJson, string started, string afterTenSeconds)
    {
        var timeProvider = new ControlledTimeProvider();
        IToolPresenter presenter = new WaitToolPresenter(timeProvider);
        var live = presenter.PresentLive(new ToolCallPresentation("wait", argumentsJson), 0);

        _ = await Assert.That(live.Render(LiveContext).Lines[0].Text).IsEqualTo(started);

        timeProvider.SetElapsed(TimeSpan.FromSeconds(10));
        _ = await Assert.That(live.Animate(1).Render(LiveContext).Lines[0].Text).IsEqualTo(afterTenSeconds);
    }

    [Test]
    public async Task Wait_is_live_only_and_modeline_eligible()
    {
        IToolPresenter presenter = new WaitToolPresenter(TimeProvider.System);

        _ = await Assert.That(presenter.Metadata.LiveOnly).IsTrue();
        _ = await Assert.That(presenter.Metadata.Modeline).IsTrue();
    }

    [Test]
    public async Task Wait_omits_terminal_output()
    {
        IToolPresenter presenter = new WaitToolPresenter(TimeProvider.System);
        var call = new ToolCallPresentation("wait", "{}");
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "timed out", string.Empty);

        _ = await Assert.That(presenter.PresentTerminal(call, terminal)).IsNull();
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void SetElapsed(TimeSpan elapsed) => _timestamp = elapsed.Ticks;
    }
}
