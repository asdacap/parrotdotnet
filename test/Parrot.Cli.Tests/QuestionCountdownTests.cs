using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class QuestionCountdownTests
{
    [Test]
    [Arguments(1001L, 1L, "Auto-return in 0:02")]
    [Arguments(60001L, 60L, "Auto-return in 1:01")]
    [Arguments(0L, 0L, "Auto-return in 0:00")]
    [Arguments(-1L, 0L, "Auto-return in 0:00")]
    public async Task Countdown_rounds_clamps_refreshes_and_omits_unknown_timing(
        long milliseconds, long afterOneSecond, string label)
    {
        var time = new CountdownTimeProvider();
        var countdown = new QuestionCountdown(time);
        _ = await Assert.That(countdown.GetRemainingSeconds()).IsNull();
        countdown.Update(milliseconds);
        var rendered = new QuestionCountdownValue(countdown.GetRemainingSeconds() ?? throw new InvalidOperationException("Missing countdown"))
            .Render(new LiveBufferRenderContext(80, new TerminalPalette(false)));
        _ = await Assert.That(rendered.Lines.Single().Text).IsEqualTo(label);
        _ = await Assert.That(rendered.Caret).IsNull();
        _ = await Assert.That(rendered.Retention).IsEqualTo(LiveBufferRetention.Fixed);

        time.Advance(TimeSpan.FromSeconds(1));
        _ = await Assert.That(countdown.GetRemainingSeconds()).IsEqualTo(afterOneSecond);
        time.Advance(TimeSpan.FromHours(1));
        _ = await Assert.That(countdown.GetRemainingSeconds()).IsEqualTo(0);
        countdown.Update(1500);
        _ = await Assert.That(countdown.GetRemainingSeconds()).IsEqualTo(2);
        countdown.Update(null);
        _ = await Assert.That(countdown.GetRemainingSeconds()).IsNull();
    }

    private sealed class CountdownTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
