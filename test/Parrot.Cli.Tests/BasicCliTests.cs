using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class BasicCliTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_line_typed_during_a_turn_is_sent_rather_than_held_until_it_ends(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced);

        // A turn that starts and never ends. The old driver blocked in
        // RenderTurn until TurnEnded, so a line typed from here could not be
        // read at all, let alone sent.
        await driver.Invoker.Publish(new Event { Id = "e1", TurnStarted = new TurnStarted { Model = "model" } });

        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("first prompt");
        await driver.Sent(1, cancellationToken);

        driver.Input.Type("typed while working");
        await driver.Sent(2, cancellationToken);

        driver.Input.End();
        _ = await driving;

        _ = await Assert.That(string.Join(" | ", driver.Invoker.Sent))
            .IsEqualTo("first prompt | typed while working");
        _ = await Assert.That(driver.Invoker.Interrupts).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Ctrl_c_stops_the_turn_once_and_then_stops_parrot(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced);

        await driver.Invoker.Publish(new Event { Id = "e1", TurnStarted = new TurnStarted { Model = "model" } });

        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("first prompt");
        await driver.Sent(1, cancellationToken);

        // The first one is the turn's, and the process is left alone.
        driver.Interrupts.Signal();

        while (driver.Invoker.Interrupts < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        _ = await Assert.That(driver.Stopping.IsCancellationRequested).IsFalse();

        // The second is not: the request is still outstanding, so asking again
        // is asking for something else.
        driver.Interrupts.Signal();

        _ = await Assert.That(driver.Stopping.IsCancellationRequested).IsTrue();
        _ = await Assert.That(driver.Invoker.Interrupts).IsEqualTo(1);

        driver.Input.End();
        _ = await driving;
    }
}
