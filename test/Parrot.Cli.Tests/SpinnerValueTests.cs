namespace Parrot.Cli.Tests;

internal sealed class SpinnerValueTests
{
    [Test]
    [Arguments(0, "thinking", "⠋ thinking")]
    [Arguments(9, "thinking", "⠏ thinking")]
    [Arguments(10, "thinking", "⠋ thinking")]
    [Arguments(11, "safe\u001b[2J\twork", "⠙ safe[2J    work")]
    public async Task Render_cycles_frames_and_sanitizes_activity(int frame, string activity, string expected) =>
        _ = await Assert.That(new SpinnerValue(activity, frame).Render()).IsEqualTo(expected);
}
