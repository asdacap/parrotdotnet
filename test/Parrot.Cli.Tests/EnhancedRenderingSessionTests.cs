using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Config;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedRenderingSessionTests
{
    [Test]
    public async Task Frame_copies_owned_input_and_places_modeline_before_prompt(
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var driver = new CliLifecycleDriver(enhanced: true);
        var terminal = new TestTerminal(driver.Input, output, error, 80);
        var configuration = new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml"));
        var input = new List<ILiveBufferItem> { new PromptValue("first> ", "draft", 0) };
        var renderer = new TerminalFrameRenderer(output, terminal.GetColumns, new TerminalPalette(false), 10, 12, true);
        var presenters = new ToolPresenterRegistry([], new GenericToolPresenter());
        using var session = new EnhancedRenderingSession(
            new EnhancedTurnRenderer(terminal, configuration, presenters),
            presenters,
            renderer,
            new TestSlashSession("provider/model"),
            input,
            static (_, _) => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static () => false,
            static (_, _) => Task.CompletedTask,
            true);

        input[0] = new PromptValue("changed> ", "not owned", 0);
        await session.Refresh(cancellationToken);

        var frame = output.ToString();
        var modeline = frame.IndexOf("mode: build", StringComparison.Ordinal);
        var prompt = frame.IndexOf("first> draft", StringComparison.Ordinal);
        _ = await Assert.That(modeline).IsGreaterThanOrEqualTo(0);
        _ = await Assert.That(prompt).IsGreaterThan(modeline);
        _ = await Assert.That(frame).DoesNotContain("changed>");
    }

    [Test]
    public async Task Run_orders_and_deduplicates_queue_snapshot_and_starts_only_main_turn(
        CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event
            {
                QueueSnapshot = new QueueSnapshot
                {
                    Revision = 1,
                    FinalChunk = true,
                    Queues =
                    {
                        new QueueState { Name = "zeta", Description = "last queue", ItemCount = 1 },
                        new QueueState { Name = "alpha", Description = "stale queue", ItemCount = 2 },
                        new QueueState { Name = "alpha", Description = "first queue", ItemCount = 3 },
                        new QueueState { Name = "empty", Description = "hidden queue", ItemCount = 0 },
                    },
                },
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await stream.WriteAsync(
            new Event
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "worker" },
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event { AgentSessionId = "child", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await stream.WriteAsync(
            new Event { AgentSessionId = "root", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        using var driver = new CliLifecycleDriver(enhanced: true);
        var terminal = new TestTerminal(driver.Input, output, error, 100);
        var configuration = new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml"));
        var mainTurns = 0;
        var renderer = new TerminalFrameRenderer(output, terminal.GetColumns, new TerminalPalette(false), 10, 12, true);
        var presenters = new ToolPresenterRegistry([], new GenericToolPresenter());
        using var session = new EnhancedRenderingSession(
            new EnhancedTurnRenderer(terminal, configuration, presenters),
            presenters,
            renderer,
            new TestSlashSession("provider/model"),
            [new PromptValue("> ", string.Empty, 0)],
            static (_, _) => Task.CompletedTask,
            _ =>
            {
                mainTurns++;
                return Task.CompletedTask;
            },
            static _ => Task.CompletedTask,
            static () => true,
            static (_, _) => Task.CompletedTask,
            true);

        var completed = await session.Run(stream.Reader, cancellationToken);
        await session.Refresh(cancellationToken);

        var frame = output.ToString();
        var alpha = frame.LastIndexOf("queue: alpha — first queue · 3 items", StringComparison.Ordinal);
        var zeta = frame.LastIndexOf("queue: zeta — last queue · 1 item", StringComparison.Ordinal);
        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(mainTurns).IsEqualTo(1);
        _ = await Assert.That(alpha).IsGreaterThanOrEqualTo(0);
        _ = await Assert.That(zeta).IsGreaterThan(alpha);
        _ = await Assert.That(frame).DoesNotContain("stale queue");
        _ = await Assert.That(frame).DoesNotContain("hidden queue");
    }
}
