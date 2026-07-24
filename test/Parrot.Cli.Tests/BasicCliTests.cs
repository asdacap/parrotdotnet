using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class BasicCliTests
{
    [Test]
    public async Task Tool_lifecycle_events_render_as_plain_lines(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event { TextChunk = new TextChunk { Fragment = "checking" } }, cancellationToken);
        await stream.WriteAsync(
            new Event { ToolStarted = new ToolStarted { ToolCallId = "call-1", ToolName = "read" } },
            cancellationToken);
        await stream.WriteAsync(
            new Event { ToolFinished = new ToolFinished { ToolCallId = "call-1", ToolName = "read" } },
            cancellationToken);
        await stream.WriteAsync(
            new Event { ToolCancelled = new ToolCancelled { ToolCallId = "call-2", ToolName = "write" } },
            cancellationToken);
        await stream.WriteAsync(
            new Event
            {
                ToolError = new ToolError { ToolCallId = "call-3", ToolName = "shell", Message = "denied" },
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event { AgentStarted = new AgentStarted { Name = "explorer" } }, cancellationToken);
        await stream.WriteAsync(
            new Event { AgentFinished = new AgentFinished { Name = "explorer" } }, cancellationToken);
        await stream.WriteAsync(
            new Event { AgentFailed = new AgentFailed { Name = "reviewer", Message = "boom" } }, cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        _ = await BasicCli.RenderTurn(stream.Reader, output, error, cancellationToken);

        _ = await Assert.That(output.ToString()).IsEqualTo(
            $"checking{Environment.NewLine}" +
            $"  tool started: read{Environment.NewLine}" +
            $"  tool finished: read{Environment.NewLine}" +
            $"  tool cancelled: write{Environment.NewLine}" +
            $"  tool error: shell: denied{Environment.NewLine}" +
            $"  agent started: explorer{Environment.NewLine}" +
            $"  agent finished: explorer{Environment.NewLine}" +
            $"  agent failed: reviewer: boom{Environment.NewLine}");
        _ = await Assert.That(error.ToString()).IsEmpty();
    }

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
