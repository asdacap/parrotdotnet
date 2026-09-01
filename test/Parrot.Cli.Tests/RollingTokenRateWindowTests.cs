using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class RollingTokenRateWindowTests
{
    [Test]
    public async Task Window_accumulates_independent_rates_and_expires_at_exact_boundary()
    {
        var time = new RateTimeProvider();
        var window = new RollingTokenRateWindow(time);

        window.Observe(300, 60);
        window.Observe(30, 0);
        _ = await Assert.That(window.Current).IsEqualTo(new TokenRate(11, 2));

        time.Advance(TimeSpan.FromSeconds(29));
        _ = await Assert.That(window.Current).IsEqualTo(new TokenRate(11, 2));
        time.Advance(TimeSpan.FromSeconds(1));
        _ = await Assert.That(window.Current).IsEqualTo(default);
    }

    [Test]
    public async Task Window_clears_after_long_clock_advance_and_reset()
    {
        var time = new RateTimeProvider();
        var window = new RollingTokenRateWindow(time);
        window.Observe(300, 60);

        time.Advance(TimeSpan.FromSeconds(30));
        _ = await Assert.That(window.Current).IsEqualTo(default);
        window.Observe(300, 60);
        window.Reset();
        _ = await Assert.That(window.Current).IsEqualTo(default);
    }

    private sealed class RateTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
