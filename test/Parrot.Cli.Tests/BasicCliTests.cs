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
            new Event
            {
                ToolFinished = new ToolFinished
                {
                    ToolCallId = "call-1",
                    ToolName = "read",
                    Result = "enhanced-only result",
                },
            },
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
        _ = await Assert.That(output.ToString()).DoesNotContain("enhanced-only result");
        _ = await Assert.That(error.ToString()).IsEmpty();
    }

    [Test]
    public async Task Turn_completion_reports_cumulative_token_totals(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event
            {
                TurnEnded = new TurnEnded { FinishReason = "stop", InputTokens = 1234, OutputTokens = 567 },
            },
            cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        var completed = await BasicCli.RenderTurn(stream.Reader, output, error, cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(output.ToString()).Contains("stop, 1234 total in / 567 total out");
        _ = await Assert.That(error.ToString()).IsEmpty();
    }

    [Test]
    public async Task Length_completion_reports_cumulative_token_totals(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event { TurnEnded = new TurnEnded { FinishReason = "length", InputTokens = 100, OutputTokens = 1 } },
            cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        var completed = await BasicCli.RenderTurn(stream.Reader, output, error, cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(output.ToString()).Contains("length, 100 total in / 1 total out");
        _ = await Assert.That(error.ToString()).IsEmpty();
    }

    [Test]
    public async Task Status_injection_renders_as_its_own_notification(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(new Event { Id = "status", StatusInjected = new StatusInjected() }, cancellationToken);
        await stream.WriteAsync(
            new Event { Id = "ended", TurnEnded = new TurnEnded { FinishReason = "stop" } }, cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        var completed = await BasicCli.RenderTurn(stream.Reader, output, error, cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(output.ToString()).Contains("↻ Status prompt injected");
        _ = await Assert.That(output.ToString()).DoesNotContain("  ↻ Status prompt injected");
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
    public async Task A_queued_turn_is_rendered_without_another_message(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced);
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("first prompt");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event { Id = "start-1", TurnStarted = new TurnStarted { Model = "model" } });

        driver.Input.Type("queued prompt");
        await driver.Sent(2, cancellationToken);
        await driver.Invoker.Publish(new Event { Id = "text-1", TextChunk = new TextChunk { Fragment = "first answer" } });
        await driver.Invoker.Publish(new Event { Id = "end-1", TurnEnded = new TurnEnded { FinishReason = "stop" } });
        await driver.Invoker.Publish(new Event { Id = "start-2", TurnStarted = new TurnStarted { Model = "model" } });
        await driver.Invoker.Publish(new Event { Id = "text-2", TextChunk = new TextChunk { Fragment = "queued answer" } });
        await driver.Invoker.Publish(new Event { Id = "end-2", TurnEnded = new TurnEnded { FinishReason = "stop" } });

        await driver.OutputContains("queued answer", cancellationToken);
        driver.Input.End();
        _ = await driving;

        _ = await Assert.That(driver.Invoker.Sent.Count).IsEqualTo(2);
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
