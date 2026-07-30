using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Parrot.Auth;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Config;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedCliTests
{
    [Test]
    public async Task Main_prompt_submits_after_one_hundred_milliseconds_of_quiet(
        CancellationToken cancellationToken)
    {
        var delay = new ControlledSubmitDelay();
        using var driver = new CliLifecycleDriver(
            true,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            delay.Wait);
        var running = driver.Drive(cancellationToken);

        driver.Input.Type("hello");
        var pending = await delay.Read(cancellationToken);

        _ = await Assert.That(pending.Interval).IsEqualTo(TimeSpan.FromMilliseconds(100));
        _ = await Assert.That(driver.Invoker.Sent).IsEmpty();

        pending.Release();
        await driver.Sent(1, cancellationToken);
        driver.Input.End();
        _ = await running;

        _ = await Assert.That(driver.Invoker.Sent).HasSingleItem();
        _ = await Assert.That(driver.Invoker.Sent[0]).IsEqualTo("hello");
    }

    [Test]
    public async Task Fast_lines_are_combined_before_the_quiet_submit(CancellationToken cancellationToken)
    {
        var delay = new ControlledSubmitDelay();
        using var driver = new CliLifecycleDriver(
            true,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            delay.Wait);
        var running = driver.Drive(cancellationToken);

        driver.Input.Type("first");
        var first = await delay.Read(cancellationToken);
        driver.Input.Type("second");
        var second = await delay.Read(cancellationToken);

        await first.Canceled.WaitAsync(cancellationToken);
        _ = await Assert.That(driver.Invoker.Sent).IsEmpty();

        second.Release();
        await driver.Sent(1, cancellationToken);
        driver.Input.End();
        _ = await running;

        _ = await Assert.That(driver.Invoker.Sent).HasSingleItem();
        _ = await Assert.That(driver.Invoker.Sent[0]).IsEqualTo("first\nsecond");
    }

    [Test]
    public async Task Repeated_fast_enter_preserves_an_interior_blank_line(CancellationToken cancellationToken)
    {
        var delay = new ControlledSubmitDelay();
        using var driver = new CliLifecycleDriver(
            true,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            delay.Wait);
        var running = driver.Drive(cancellationToken);

        driver.Input.Type("first");
        var first = await delay.Read(cancellationToken);
        driver.Input.Type(string.Empty);
        var blank = await delay.Read(cancellationToken);
        driver.Input.Type("second");
        var second = await delay.Read(cancellationToken);

        await first.Canceled.WaitAsync(cancellationToken);
        await blank.Canceled.WaitAsync(cancellationToken);
        second.Release();
        await driver.Sent(1, cancellationToken);
        driver.Input.End();
        _ = await running;

        _ = await Assert.That(driver.Invoker.Sent[0]).IsEqualTo("first\n\nsecond");
    }

    [Test]
    [Arguments("\u001b")]
    [Arguments("\u0003")]
    public async Task Interrupt_keys_preserve_draft_input(string key, CancellationToken cancellationToken)
    {
        using var terminal = new ScriptedTerminal(80);
        using var stopping = new CancellationTokenSource();
        using var http = new HttpClient();
        var invoker = new ScriptedInvoker();
        var cli = new EnhancedCli(
            new GeneratedParrot.ParrotClient(invoker),
            new Interrupts(stopping),
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            new UnusedCredentials(),
            new OpenAiOAuthClient(http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml")),
            ["provider"],
            terminal,
            Presenters(),
            ImmediateDelay());
        var running = cli.Run(cancellationToken);

        terminal.Type("first prompt\r");
        await Sent(invoker, 1, cancellationToken);
        terminal.Type("draft prompt");
        terminal.Type(key);
        terminal.Tick();

        while (invoker.Interrupts < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        _ = await Assert.That(stopping.IsCancellationRequested).IsFalse();
        terminal.Type("\r");
        await Sent(invoker, 2, cancellationToken);
        terminal.End();
        _ = await running;

        _ = await Assert.That(invoker.Interrupts).IsEqualTo(1);
        _ = await Assert.That(string.Join('|', invoker.Sent)).IsEqualTo("first prompt|draft prompt");
    }

    [Test]
    public async Task Plan_completion_renders_markdown_and_approves_with_picker(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var running = driver.Drive(cancellationToken);
        driver.Input.Type("draft plan");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            TurnStarted = new TurnStarted { Model = "model" },
        });
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            PlanCompleted = new PlanCompleted
            {
                Markdown = "# Written plan\n\n- change code",
                Dialog = new TurnCompleteDialog
                {
                    Prompt = "Plan complete: ",
                    CustomChoice = "feedback",
                    CustomPrompt = "Feedback: ",
                    CustomDescription = "Revise the plan",
                    Choices =
                    {
                        new DialogChoice
                        {
                            Value = "yes",
                            Description = "Implement it",
                            Action = new ChoiceAction { Mode = "build", Prompt = "Implement the approved plan." },
                        },
                        new DialogChoice { Value = "no", Description = "Keep planning" },
                    },
                },
            },
        });
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            TurnEnded = new TurnEnded { FinishReason = "stop" },
        });

        await driver.OutputContains("Written plan", cancellationToken);
        await driver.OutputContains("Plan complete:", cancellationToken);
        var outputBeforeApproval = driver.Output.Length;
        driver.Input.Type("yes");
        await driver.Sent(2, cancellationToken);
        await driver.OutputContainsAfter(outputBeforeApproval, "\u001b[2K❯ ", cancellationToken);

        _ = await Assert.That(driver.Invoker.Sent.Count).IsEqualTo(2);
        _ = await Assert.That(driver.Invoker.Sent[0]).IsEqualTo("draft plan");
        _ = await Assert.That(driver.Invoker.Sent[1]).IsEqualTo("Implement the approved plan.");
        _ = await Assert.That(driver.Invoker.Updated).Count().IsEqualTo(1);
        _ = await Assert.That(driver.Invoker.Updated[0].Mode).IsEqualTo("build");

        driver.Input.End();
        _ = await running;
    }

    [Test]
    public async Task Initial_plan_turn_shows_the_completion_dialog(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(
            true,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "plan" }, "draft plan"));
        var running = driver.Drive(cancellationToken);
        await driver.Sent(1, cancellationToken);
        _ = await Assert.That(driver.Invoker.Created.Single().InteractivePermissions).IsFalse();
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            TurnStarted = new TurnStarted { Model = "model" },
        });
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            PlanCompleted = new PlanCompleted
            {
                Markdown = "# Written plan\n\n- change code",
                Dialog = new TurnCompleteDialog
                {
                    Prompt = "Plan complete: ",
                    Choices =
                    {
                        new DialogChoice
                        {
                            Value = "yes",
                            Description = "Implement it",
                            Action = new ChoiceAction { Mode = "build", Prompt = "Implement the approved plan." },
                        },
                    },
                },
            },
        });
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            TurnEnded = new TurnEnded { FinishReason = "stop" },
        });

        await driver.OutputContains("Written plan", cancellationToken);
        await driver.OutputContains("Plan complete:", cancellationToken);
        driver.Input.Type("yes");
        await driver.Sent(2, cancellationToken);

        _ = await Assert.That(driver.Invoker.Updated).Count().IsEqualTo(1);
        _ = await Assert.That(driver.Invoker.Updated[0].Mode).IsEqualTo("build");
        _ = await Assert.That(driver.Invoker.Sent[1]).IsEqualTo("Implement the approved plan.");

        driver.Input.End();
        _ = await running;
    }

    [Test]
    public async Task Question_arrival_wakes_input_loop_and_renders_picker(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var running = driver.Drive(cancellationToken);
        driver.Input.Type("ask me");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            TurnStarted = new TurnStarted { Model = "model" },
        });
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            ToolStarted = new ToolStarted { ToolCallId = "call-question", ToolName = "question" },
        });

        while (driver.Invoker.PendingQuestionLists < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        var pending = new PendingQuestion { Id = "question-request" };
        pending.Questions.Add(new QuestionDefinition
        {
            Id = "colour",
            Header = "Question",
            Prompt = "Pick a colour",
            Options = { new QuestionOption { Id = "blue", Label = "Blue" } },
        });
        driver.Invoker.AddPendingQuestion(pending);

        await driver.OutputContains("Pick a colour", cancellationToken);
        driver.Input.Type("blue");
        while (driver.Invoker.QuestionReplies.Count < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        var reply = driver.Invoker.QuestionReplies.Single();
        _ = await Assert.That(reply.QuestionRequestId).IsEqualTo("question-request");
        _ = await Assert.That(reply.Answers.Single().QuestionId).IsEqualTo("colour");
        _ = await Assert.That(reply.Answers.Single().OptionIds.Single()).IsEqualTo("blue");

        driver.Input.End();
        _ = await running;
    }

    [Test]
    public async Task Typed_turn_events_render_cumulative_text_and_sanitize_terminal_content(
        CancellationToken cancellationToken)
    {
        var (completed, output, error) = await Render(
            [
                new Event
                {
                    Id = "admitted-before-turn",
                    InputAdmitted = new InputAdmitted { Content = "already visible" },
                },
                new Event { Id = "start", TurnStarted = new TurnStarted { Model = "model" } },
                new Event { Id = "status", StatusInjected = new StatusInjected() },
                new Event
                {
                    Id = "queued",
                    InputAdmitted = new InputAdmitted { Content = "next\u001b[2J\tline" },
                },
                new Event { Id = "thought-1", ReasoningChunk = new ReasoningChunk { Fragment = "think" } },
                new Event
                {
                    Id = "thought-2",
                    ReasoningChunk = new ReasoningChunk { Fragment = "\u001b]0;title\n" },
                },
                new Event { Id = "text-1", TextChunk = new TextChunk { Fragment = "abcdefgh" } },
                new Event { Id = "text-2", TextChunk = new TextChunk { Fragment = "ij" } },
                new Event
                {
                    Id = "tool-chunk",
                    ToolCallChunk = new ToolCallChunk { ToolName = "unrendered" },
                },
                new Event
                {
                    Id = "tool-started",
                    ToolStarted = new ToolStarted { ToolCallId = "call-1", ToolName = "shell\u001b[31m" },
                },
                new Event
                {
                    Id = "tool-finished",
                    ToolFinished = new ToolFinished { ToolCallId = "call-1", ToolName = "shell\u001b[31m" },
                },
                new Event
                {
                    Id = "tool-cancelled",
                    ToolCancelled = new ToolCancelled { ToolCallId = "call-2", ToolName = "read\u001b[2J" },
                },
                new Event
                {
                    Id = "tool-error",
                    ToolError = new ToolError
                    {
                        ToolCallId = "call-3",
                        ToolName = "write\u001b[31m",
                        Message = "denied\u001b[2J",
                    },
                },
                new Event
                {
                    Id = "agent-started",
                    AgentStarted = new AgentStarted { Name = "explorer\u001b[31m" },
                },
                new Event
                {
                    Id = "agent-finished",
                    AgentFinished = new AgentFinished { Name = "explorer\u001b[31m" },
                },
                new Event
                {
                    Id = "agent-failed",
                    AgentFailed = new AgentFailed { Name = "reviewer\u001b[31m", Message = "boom\u001b[2J" },
                },
                new Event { Id = "text-3", TextChunk = new TextChunk { Fragment = "tail" } },
                new Event
                {
                    Id = "ended",
                    TurnEnded = new TurnEnded { FinishReason = "stop\u001b[2J", InputTokens = 3, OutputTokens = 4 },
                },
            ],
            cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(error).IsEmpty();
        _ = await Assert.That(output).Contains("  input admitted: already visible");
        _ = await Assert.That(output).Contains("  turn started: model");
        _ = await Assert.That(output).Contains("↻ Status prompt injected");
        _ = await Assert.That(output).DoesNotContain("  ↻ Status prompt injected");
        _ = await Assert.That(output).Contains("  queued: next[2J    line");
        _ = await Assert.That(output).DoesNotContain("think]0;title");
        _ = await Assert.That(output).Contains("● abcdef\r\n  ghij\r\n");
        _ = await Assert.That(output).Contains("  tool call unrendered:");
        _ = await Assert.That(output).Contains("  * shell[31m started");
        _ = await Assert.That(output).Contains("  + shell[31m finished");
        _ = await Assert.That(output).Contains("  - read[2J cancelled");
        _ = await Assert.That(output).Contains("  ! write[31m: denied[2J");
        _ = await Assert.That(output).Contains("  * agent explorer[31m started");
        _ = await Assert.That(output).Contains("  + agent explorer[31m finished");
        _ = await Assert.That(output).Contains("  ! agent reviewer[31m: boom[2J");
        _ = await Assert.That(output).Contains("● tail\r\n");
        _ = await Assert.That(output).Contains("  stop[2J - 3 total in / 4 total out");
        _ = await Assert.That(Count(output, "● abcdef\r\n")).IsEqualTo(1);
        _ = await Assert.That(UntrustedEscape(output)).IsFalse();
        _ = await Assert.That(output).DoesNotContain("\u001b[?1049");
    }

    [Test]
    public async Task Summary_reasoning_chunks_are_committed_individually(CancellationToken cancellationToken)
    {
        var (completed, output, error) = await Render(
            [
                new Event { Id = "start", TurnStarted = new TurnStarted { Model = "model" } },
                new Event
                {
                    Id = "summary-1",
                    ReasoningChunk = new ReasoningChunk
                    {
                        Fragment = "# first\n- **bold**",
                        Kind = ReasoningKind.Summary,
                    },
                },
                new Event
                {
                    Id = "summary-2",
                    ReasoningChunk = new ReasoningChunk
                    {
                        Fragment = "**safe\u001b[2J**",
                        Kind = ReasoningKind.Summary,
                    },
                },
                new Event { Id = "ended", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            ],
            cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(error).IsEmpty();
        _ = await Assert.That(output).Contains("✦ first\r\n  • bold\r\n✦ safe[2\r\n  J\r\n");
        _ = await Assert.That(output).DoesNotContain("# first");
        _ = await Assert.That(output).DoesNotContain("**bold**");
        _ = await Assert.That(output).DoesNotContain("\u001b[2J");
    }

    [Test]
    public async Task Split_markdown_chunks_are_promoted_to_scrollback_once(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        Event[] events =
        [
            new Event { Id = "start", TurnStarted = new TurnStarted { Model = "model" } },
            new Event { Id = "text-1", TextChunk = new TextChunk { Fragment = "# Head" } },
            new Event { Id = "text-2", TextChunk = new TextChunk { Fragment = "ing\n**bo" } },
            new Event { Id = "text-3", TextChunk = new TextChunk { Fragment = "ld**" } },
            new Event { Id = "ended", TurnEnded = new TurnEnded { FinishReason = "stop" } },
        ];
        foreach (var published in events)
        {
            await stream.WriteAsync(published, cancellationToken);
        }

        stream.Complete();
        using var output = new StringWriter();
        using var error = new StringWriter();

        using var driver = new CliLifecycleDriver(enhanced: true);
        var terminal = new TestTerminal(driver.Input, output, error, 80);
        var completed = await new EnhancedCli(
            new GeneratedParrot.ParrotClient(driver.Invoker),
            driver.Interrupts,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            new UnusedCredentials(),
            new OpenAiOAuthClient(driver.Http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml")),
            ["provider"],
            terminal,
            Presenters(),
            ImmediateDelay()).RenderTurn(stream.Reader, cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(error.ToString()).IsEmpty();
        _ = await Assert.That(output.ToString()).Contains("Heading\r\n");
        _ = await Assert.That(output.ToString()).Contains("bold\r\n");
        _ = await Assert.That(Count(output.ToString(), "Heading\r\n")).IsEqualTo(1);
        _ = await Assert.That(Count(output.ToString(), "bold\r\n")).IsEqualTo(1);
    }

    [Test]
    public async Task Before_render_runs_before_each_event(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        Event[] events =
        [
            new Event { Id = "none" },
            new Event { Id = "admitted-before-turn", InputAdmitted = new InputAdmitted { Content = "sent" } },
            new Event { Id = "promoted", InputPromoted = new InputPromoted { InputId = "input" } },
            new Event { Id = "retry", RetryNotice = new RetryNotice { Attempt = 1 } },
            new Event { Id = "started", TurnStarted = new TurnStarted { Model = "model" } },
            new Event { Id = "tool-chunk", ToolCallChunk = new ToolCallChunk { ToolName = "shell" } },
            new Event { Id = "first-visible", TextChunk = new TextChunk { Fragment = "answer" } },
            new Event { Id = "later-visible", ToolStarted = new ToolStarted { ToolName = "shell" } },
            new Event { Id = "ended", TurnEnded = new TurnEnded { FinishReason = "stop" } },
        ];
        foreach (var published in events)
        {
            await stream.WriteAsync(published, cancellationToken);
        }

        stream.Complete();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var callbackIds = new List<string>();

        async Task BeforeRender(Event published, CancellationToken token)
        {
            callbackIds.Add(published.Id);
            await output.WriteAsync($"before:{published.Id}|".AsMemory(), token);
        }

        using var driver = new CliLifecycleDriver(enhanced: true);
        var terminal = new TestTerminal(driver.Input, output, error, 80);
        var completed = await new EnhancedCli(
            new GeneratedParrot.ParrotClient(driver.Invoker),
            driver.Interrupts,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            new UnusedCredentials(),
            new OpenAiOAuthClient(driver.Http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml")),
            ["provider"],
            terminal,
            Presenters(),
            ImmediateDelay()).RenderTurn(stream.Reader, BeforeRender, cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(string.Join(',', callbackIds))
            .IsEqualTo("none,admitted-before-turn,promoted,retry,started,tool-chunk,first-visible,later-visible,ended");
        _ = await Assert.That(output.ToString()).StartsWith("before:none|");
        _ = await Assert.That(output.ToString()).Contains("event none has no payload");
        _ = await Assert.That(output.ToString()).Contains("input admitted: sent");
        _ = await Assert.That(output.ToString()).Contains("input promoted: input");
        _ = await Assert.That(output.ToString()).Contains("retry 1 in 0 ms:");
        _ = await Assert.That(output.ToString()).Contains("turn started: model");
        _ = await Assert.That(output.ToString()).Contains("tool call shell:");
    }

    [Test]
    public async Task Finished_shell_tool_flushes_its_command_to_scrollback(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 80, new TerminalPalette(false), 10, 12, true);
        var fixedItems = new ILiveBufferItem[]
        {
            new ModelineValue("build", "working", "provider/model"),
            new PromptValue("> ", string.Empty, 0),
        };
        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token) =>
            renderer.Draw([.. items, .. fixedItems], token);
        Task Commit(
            IScrollbackItem scrollback,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken token) => renderer.Commit(scrollback, [.. items, .. fixedItems], token);
        using var view = new RawActivityView(
            Draw,
            Commit,
            new ToolPresenterRegistry([new ExecCommandToolPresenter()], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);

        await view.Render(
            new Event
            {
                AgentSessionId = "main-session",
                TurnStarted = new TurnStarted { Model = "model" },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "main-session",
                ToolCallChunk = new ToolCallChunk
                {
                    ToolCallId = "call-1",
                    ToolName = "exec_command",
                    ArgumentsFragment = "{\"command\":\"dotnet test\"}",
                },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "main-session",
                ToolFinished = new ToolFinished
                {
                    ToolCallId = "call-1",
                    ToolName = "exec_command",
                    Result = "Process exited with code 0\nall tests passed",
                },
            },
            cancellationToken);

        var rendered = output.ToString();
        _ = await Assert.That(rendered).Contains("✓ $ dotnet test\r\n");
        _ = await Assert.That(rendered).Contains("Process exited with code 0\r\nall tests passed\r\n");
        _ = await Assert.That(rendered).DoesNotContain("exec_command finished");
    }

    [Test]
    public async Task Live_status_tracks_concurrent_agent_turns_and_owner_qualified_tools(
        CancellationToken cancellationToken)
    {
        var draws = new ConcurrentQueue<string>();
        var committed = new ConcurrentQueue<string>();
        var ticks = Channel.CreateUnbounded<bool>();
        var context = new LiveBufferRenderContext(120, new TerminalPalette(false));

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            draws.Enqueue(RenderItems(items));
            return Task.CompletedTask;
        }

        Task Commit(
            IScrollbackItem scrollback,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            committed.Enqueue(string.Join('|', scrollback.Render(new ScrollbackRenderContext(120, context.Palette))));
            draws.Enqueue(RenderItems(items));
            return Task.CompletedTask;
        }

        async Task Delay(CancellationToken token) =>
            _ = await ticks.Reader.ReadAsync(token);

        string RenderItems(IReadOnlyList<ILiveBufferItem> items) =>
            string.Join('|', items.SelectMany(item => item.Render(context).Lines).Select(line => line.Text));

        var mainActivities = new List<string>();
        Task UpdateMainAgentActivity(string activity, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            mainActivities.Add(activity);
            return Task.CompletedTask;
        }

        using var view = new RawActivityView(Draw, Commit, Delay, Presenters(), UpdateMainAgentActivity);
        using var animating = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var animation = view.Run(animating.Token);

        await view.Render(
            new Event { AgentSessionId = "stale-session", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "main-session", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "main-session",
                AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
                {
                    InputTokens = 2_400,
                    CachedInputTokens = 1_600,
                    OutputTokens = 600,
                    ContextSize = 3_000,
                    ContextLimit = 128_000,
                },
            },
            cancellationToken);
        _ = await Assert.That(mainActivities[^1]).IsEqualTo("agent main");
        _ = await Assert.That(mainActivities[^1]).DoesNotContain("in / ");
        _ = await Assert.That(mainActivities[^1]).DoesNotContain("ctx");
        await view.Render(
            new Event
            {
                AgentSessionId = "child-session",
                AgentStarted = new AgentStarted
                {
                    ParentAgentSessionId = "main-session",
                    Name = "explorer\u001b[31m",
                },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child-session", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child-session",
                AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
                {
                    InputTokens = 1200000,
                    CachedInputTokens = 800,
                    OutputTokens = 300,
                    ContextSize = 1500,
                    ContextLimit = 0,
                },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "main-session",
                ToolCallChunk = new ToolCallChunk
                {
                    ToolCallId = "shared-call",
                    ToolName = "exec_command",
                    ArgumentsFragment = "dotnet test",
                },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "main-session",
                ToolStarted = new ToolStarted { ToolCallId = "shared-call", ToolName = "exec_command" },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child-session",
                ToolStarted = new ToolStarted { ToolCallId = "shared-call", ToolName = "read\u001b[2J" },
            },
            cancellationToken);
        await view.ReplaceContent([new LiveTextValue("answer")], cancellationToken);

        var live = draws.Last();
        _ = await Assert.That(live).Contains("answer");
        _ = await Assert.That(live).DoesNotContain("agent main");
        _ = await Assert.That(mainActivities).Contains("agent main");
        _ = await Assert.That(live).Contains("  ⠋ [explorer[31m] agent explorer[31m (1.2m in / 800 cached / 300 out, 1.5k/? ctx)");
        _ = await Assert.That(live).Contains("⠋ exec_command");
        _ = await Assert.That(live).Contains("  ⠋ [explorer[31m] read[2J");

        await ticks.Writer.WriteAsync(true, cancellationToken);
        while (draws.Count < 2)
        {
            await Task.Delay(1, cancellationToken);
        }

        await view.Render(
            new Event
            {
                AgentSessionId = "main-session",
                ToolFinished = new ToolFinished { ToolCallId = "shared-call", ToolName = "exec_command" },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child-session",
                ToolError = new ToolError
                {
                    ToolCallId = "shared-call",
                    ToolName = "read",
                    Message = "denied\u001b[2J",
                },
            },
            cancellationToken);
        await view.CommitContent(ImmediateScrollbackValue.Muted(["answer"]), [], cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "main-session", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        _ = await Assert.That(mainActivities.Last()).IsEmpty();
        await view.ReplaceContent([], cancellationToken);
        _ = await Assert.That(draws.Last()).Contains("agent explorer[31m");
        _ = await Assert.That(draws.Last()).DoesNotContain("agent main");

        await view.Render(
            new Event { AgentSessionId = "child-session", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        var beforeAgentFinished = committed.Count;
        await view.Render(
            new Event
            {
                AgentSessionId = "child-session",
                AgentFinished = new AgentFinished
                {
                    ParentAgentSessionId = "main-session",
                    Name = "explorer",
                },
            },
            cancellationToken);

        await animating.CancelAsync();
        await animation;

        _ = await Assert.That(beforeAgentFinished).IsEqualTo(5);
        _ = await Assert.That(committed.Count).IsEqualTo(5);
        _ = await Assert.That(string.Join('|', committed)).Contains("✓ tool call exec_command|dotnet test");
        _ = await Assert.That(string.Join('|', committed)).Contains("  ✗ [explorer[31m] tool call read[2J|    [explorer[31m] denied[2J");
        _ = await Assert.That(string.Join('|', committed)).Contains("  ♟ [explorer[31m] agent finished");
        _ = await Assert.That(draws.Last()).IsEmpty();
    }

    [Test]
    public async Task Enhanced_turn_ignores_agent_statistics_events(CancellationToken cancellationToken)
    {
        var (completed, output, error) = await Render(
            [
                new Event { AgentSessionId = "main", TurnStarted = new TurnStarted { Model = "model" } },
                new Event
                {
                    AgentSessionId = "main",
                    AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
                    {
                        InputTokens = 10,
                        CachedInputTokens = 5,
                        OutputTokens = 2,
                        ContextSize = 12,
                        ContextLimit = 100,
                    },
                },
                new Event
                {
                    AgentSessionId = "main",
                    TurnEnded = new TurnEnded { FinishReason = "stop", InputTokens = 10, OutputTokens = 2 },
                },
            ],
            cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(output).DoesNotContain("AgentStatisticsUpdated");
        _ = await Assert.That(output).Contains("stop - 10 total in / 2 total out");
        _ = await Assert.That(error).IsEmpty();
    }

    [Test]
    public async Task Child_turn_completion_does_not_complete_the_foreground_turn(
        CancellationToken cancellationToken)
    {
        var (completed, output, error) = await Render(
            [
                new Event
                {
                    Id = "main-start",
                    AgentSessionId = "main-session",
                    TurnStarted = new TurnStarted { Model = "model" },
                },
                new Event
                {
                    Id = "child-started",
                    AgentSessionId = "child-session",
                    AgentStarted = new AgentStarted
                    {
                        ParentAgentSessionId = "main-session",
                        Name = "explorer",
                    },
                },
                new Event
                {
                    Id = "child-turn",
                    AgentSessionId = "child-session",
                    TurnStarted = new TurnStarted { Model = "model" },
                },
                new Event
                {
                    Id = "child-ended",
                    AgentSessionId = "child-session",
                    TurnEnded = new TurnEnded { FinishReason = "child-stop" },
                },
                new Event
                {
                    Id = "main-text",
                    AgentSessionId = "main-session",
                    TextChunk = new TextChunk { Fragment = "main continues" },
                },
                new Event
                {
                    Id = "main-ended",
                    AgentSessionId = "main-session",
                    TurnEnded = new TurnEnded { FinishReason = "main-stop" },
                },
            ],
            cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(error).IsEmpty();
        var childEnded = output.IndexOf("child-stop", StringComparison.Ordinal);
        var mainContinued = output.IndexOf("● main c", StringComparison.Ordinal);
        var mainEnded = output.IndexOf("main-stop", StringComparison.Ordinal);
        _ = await Assert.That(childEnded).IsEqualTo(-1);
        _ = await Assert.That(mainContinued).IsGreaterThanOrEqualTo(0);
        _ = await Assert.That(mainEnded).IsGreaterThan(mainContinued);
    }

    [Test]
    public async Task Turn_view_events_replace_cached_content_without_drawing_the_terminal(
        CancellationToken cancellationToken)
    {
        var replacements = new List<string>();
        var committed = new List<string>();
        var liveContext = new LiveBufferRenderContext(80, new TerminalPalette(false));
        var scrollbackContext = new ScrollbackRenderContext(80, liveContext.Palette);

        Task Replace(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            replacements.Add(string.Join('|', items.SelectMany(item => item.Render(liveContext).Lines)
                .Select(static line => line.Text)));
            return Task.CompletedTask;
        }

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            committed.Add(string.Join('|', item.Render(scrollbackContext)));
            return Task.CompletedTask;
        }

        using var error = new StringWriter();
        var view = new EnhancedTurnView(Replace, Commit, error, static () => 80, true, false, new ForegroundTurn());
        var text = new Event { TextChunk = new TextChunk { Fragment = "pending" } };
        await view.Prepare(text, cancellationToken);
        _ = await view.Render(text, cancellationToken);
        var reasoning = new Event { ReasoningChunk = new ReasoningChunk { Fragment = "thinking" } };
        await view.Prepare(reasoning, cancellationToken);
        _ = await view.Render(reasoning, cancellationToken);

        _ = await Assert.That(replacements).Contains("● pending");
        _ = await Assert.That(replacements.Any(static value => value.Contains("thinking", StringComparison.Ordinal))).IsTrue();
        _ = await Assert.That(committed).Contains("● pending");
    }

    [Test]
    public async Task Cancel_retries_the_same_stream_completion_after_commit_cancellation(
        CancellationToken cancellationToken)
    {
        var committed = new List<IScrollbackItem>();
        var attempts = 0;

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token) => Task.CompletedTask;

        Task Commit(
            IScrollbackItem item,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken token)
        {
            committed.Add(item);
            attempts++;
            return attempts == 2
                ? Task.FromException(new OperationCanceledException())
                : Task.CompletedTask;
        }

        using var error = new StringWriter();
        var view = new EnhancedTurnView(Draw, Commit, error, static () => 80, false, false, new ForegroundTurn());
        _ = await view.Render(
            new Event { TextChunk = new TextChunk { Fragment = "complete line\nsuffix" } },
            cancellationToken);

        _ = await Assert.That(async () => await view.Prepare(
            new Event { TurnEnded = new TurnEnded() },
            cancellationToken)).Throws<OperationCanceledException>();
        await view.Cancel(CancellationToken.None);

        _ = await Assert.That(committed.Count).IsEqualTo(3);
        _ = await Assert.That(ReferenceEquals(committed[1], committed[2])).IsTrue();
        _ = await Assert.That(committed[2].Continues(committed[0])).IsTrue();
    }

    [Test]
    public async Task Interactive_chat_updates_the_prompt_while_a_turn_is_busy(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("first prompt");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event { Id = "start", TurnStarted = new TurnStarted { Model = "model" } });

        driver.Input.Type("steer while busy");
        await driver.OutputContains("steer while busy", cancellationToken);
        await driver.Sent(2, cancellationToken);

        driver.Input.End();
        _ = await driving;
    }

    [Test]
    public async Task Slash_command_completion_filters_selects_and_dispatches(CancellationToken cancellationToken)
    {
        using var terminal = new ScriptedTerminal(80);
        using var stopping = new CancellationTokenSource();
        using var http = new HttpClient();
        var invoker = new ScriptedInvoker();
        var cli = new EnhancedCli(
            new GeneratedParrot.ParrotClient(invoker),
            new Interrupts(stopping),
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            new UnusedCredentials(),
            new OpenAiOAuthClient(http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml")),
            ["provider"],
            terminal,
            Presenters(),
            ImmediateDelay());
        var running = cli.Run(cancellationToken);

        terminal.Type("/m");
        await OutputContains(terminal, "Switch the mode for this session", cancellationToken);
        await OutputContains(terminal, "Switch the model for this session", cancellationToken);
        var filtered = terminal.Output.ToString() ?? throw new InvalidOperationException("terminal output is unavailable");
        var filteredAt = filtered.LastIndexOf("Switch the mode for this session", StringComparison.Ordinal);
        _ = await Assert.That(filtered[filteredAt..]).DoesNotContain("Leave the session");

        terminal.Type("\u001b[B\t");
        await OutputContains(terminal, "❯ /model", cancellationToken);
        _ = await Assert.That(invoker.Sent).IsEmpty();

        terminal.Type("\u0001\u000b/exit\r");
        _ = await running.WaitAsync(cancellationToken);
    }

    [Test]
    public async Task Shift_tab_cycles_through_foreground_modes(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("\u001b[Z");
        while (driver.Invoker.Updated.Count < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        driver.Input.Type("\u001b[Z");
        while (driver.Invoker.Updated.Count < 2)
        {
            await Task.Delay(5, cancellationToken);
        }

        driver.Input.Type("\u001b[Z");
        while (driver.Invoker.Updated.Count < 3)
        {
            await Task.Delay(5, cancellationToken);
        }

        driver.Input.End();
        _ = await driving;

        _ = await Assert.That(driver.Invoker.Updated.Count).IsEqualTo(3);
        _ = await Assert.That(driver.Invoker.Updated[0].Mode).IsEqualTo("plan");
        _ = await Assert.That(driver.Invoker.Updated[1].Mode).IsEqualTo("query");
        _ = await Assert.That(driver.Invoker.Updated[2].Mode).IsEqualTo("build");
    }

    [Test]
    public async Task Shift_tab_from_query_wraps_to_build(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(
            enhanced: true,
            new EnhancedChatRequest(
                new CreateSessionRequest { Model = "provider/model", Mode = "query" },
                string.Empty));
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("\u001b[Z");
        while (driver.Invoker.Updated.Count < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        driver.Input.End();
        _ = await driving;

        _ = await Assert.That(driver.Invoker.Updated).HasSingleItem();
        _ = await Assert.That(driver.Invoker.Updated[0].Mode).IsEqualTo("build");
    }

    [Test]
    public async Task Queue_inventory_is_visible_before_the_first_turn(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        driver.Invoker.SetInitialQueues(
            "session-1",
            new QueueState { Name = "release", Description = "release tasks", ItemCount = 3 });

        var driving = driver.Drive(cancellationToken);

        await driver.OutputContains("queue: release tasks · 3 items", cancellationToken);
        driver.Input.End();
        _ = await driving;
    }

    [Test]
    public async Task Queue_inventory_keeps_more_than_ten_fixed_rows(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        driver.Invoker.SetInitialQueues(
            "session-1",
            [.. Enumerable.Range(1, 12).Select(index => new QueueState
            {
                Name = $"queue-{index:D2}",
                Description = $"work list {index:D2}",
                ItemCount = index,
            })]);

        var driving = driver.Drive(cancellationToken);

        await driver.OutputContains("queue: work list 12 · 12 items", cancellationToken);
        driver.Input.End();
        _ = await driving;
    }

    [Test]
    public async Task Queue_inventory_updates_while_turn_text_is_streaming(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("prompt");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event
        {
            Id = "start",
            AgentSessionId = "agent",
            TurnStarted = new TurnStarted { Model = "model" },
        });
        await driver.Invoker.Publish(new Event
        {
            Id = "text",
            AgentSessionId = "agent",
            TextChunk = new TextChunk { Fragment = "partial answer" },
        });
        await driver.Invoker.Publish(new Event
        {
            QueueSnapshot = new QueueSnapshot
            {
                Revision = 1,
                FinalChunk = true,
                Queues = { new QueueState { Name = "work", Description = "pending work", ItemCount = 2 } },
            },
        });

        await driver.OutputContains("partial answer", cancellationToken);
        await driver.OutputContains("queue: pending work · 2 items", cancellationToken);
        await driver.Invoker.Publish(new Event
        {
            QueueSnapshot = new QueueSnapshot { Revision = 2, FinalChunk = true },
        });
        await driver.Invoker.Publish(new Event
        {
            Id = "end",
            AgentSessionId = "agent",
            TurnEnded = new TurnEnded { FinishReason = "stop" },
        });

        driver.Input.End();
        _ = await driving;
    }

    [Test]
    public async Task Spinner_rearms_for_each_turn_and_shutdown_joins_the_active_spinner(
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("first prompt");
        await driver.Sent(1, cancellationToken);
        await driver.OutputContains("thinking", cancellationToken);
        await driver.Invoker.Publish(
            new Event { Id = "start-1", AgentSessionId = "agent", TurnStarted = new TurnStarted { Model = "model" } });
        await driver.Invoker.Publish(
            new Event { Id = "ended-1", AgentSessionId = "agent", TurnEnded = new TurnEnded { FinishReason = "stop" } });

        var secondTurnOutput = driver.Output.Length;
        driver.Input.Type("second prompt");
        await driver.Sent(2, cancellationToken);
        await driver.OutputContainsAfter(secondTurnOutput, "thinking", cancellationToken);

        driver.Input.End();
        _ = await driving.WaitAsync(cancellationToken);
    }

    [Test]
    public async Task Interactive_chat_accepts_another_turn_after_a_failure(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("first prompt");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event { Id = "start-1", TurnStarted = new TurnStarted { Model = "model" } });
        await driver.Invoker.Publish(
            new Event { Id = "failed-1", TurnFailed = new TurnFailed { Message = "tool-call limit" } });
        await driver.ErrorContains("tool-call limit", cancellationToken);

        driver.Input.Type("second prompt");
        await driver.Sent(2, cancellationToken);
        await driver.Invoker.Publish(new Event { Id = "start-2", TurnStarted = new TurnStarted { Model = "model" } });
        await driver.Invoker.Publish(
            new Event { Id = "text-2", TextChunk = new TextChunk { Fragment = "recovered answer" } });
        await driver.Invoker.Publish(
            new Event { Id = "ended-2", TurnEnded = new TurnEnded { FinishReason = "stop" } });

        await driver.OutputContains("recovered answer", cancellationToken);
        driver.Input.End();
        _ = await driving;

        _ = await Assert.That(driver.Invoker.Sent.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Slash_dialog_restores_input_rebinds_the_stream_and_exit_unwinds_terminal_cleanup(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced);
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("/clear");
        if (enhanced)
        {
            await driver.OutputContains("Select a provider", cancellationToken);
        }

        driver.Input.Type("provider");
        driver.Input.Type("model");
        driver.Input.Type("query");
        driver.Input.Type(string.Empty);
        driver.Input.Type("new prompt");
        await driver.Sent(1, cancellationToken);
        driver.Input.Type("/exit");

        var exitCode = await driving.WaitAsync(cancellationToken);

        _ = await Assert.That(exitCode).IsEqualTo(CommandDispatcher.ExitSuccess);
        _ = await Assert.That(driver.Invoker.Created.Count).IsEqualTo(2);
        _ = await Assert.That(driver.Invoker.Created[1].Model).IsEqualTo("provider/model");
        _ = await Assert.That(driver.Invoker.Created[1].Mode).IsEqualTo("query");
        _ = await Assert.That(driver.Invoker.Created[0].InteractivePermissions).IsTrue();
        _ = await Assert.That(driver.Invoker.Created[1].InteractivePermissions).IsTrue();
        _ = await Assert.That(string.Join('|', driver.Invoker.ListenedTo)).IsEqualTo("session-1|session-2");
        _ = await Assert.That(string.Join('|', driver.Invoker.Sent)).IsEqualTo("new prompt");
        _ = await Assert.That(string.Join('|', driver.Invoker.SentTo)).IsEqualTo("session-2");
        if (enhanced)
        {
            _ = await Assert.That(driver.Output).EndsWith("\u001b[<u\u001b[?2004l");
        }
    }

    [Test]
    public async Task Failure_commits_the_live_suffix_and_reports_sanitized_error(
        CancellationToken cancellationToken)
    {
        var (completed, output, error) = await Render(
            [
                new Event { Id = "start", TurnStarted = new TurnStarted { Model = "model" } },
                new Event { Id = "text", TextChunk = new TextChunk { Fragment = "partial" } },
                new Event
                {
                    Id = "failed",
                    TurnFailed = new TurnFailed { Message = "bad\u001b[2J\trequest" },
                },
            ],
            cancellationToken);

        _ = await Assert.That(completed).IsFalse();
        _ = await Assert.That(output).Contains("● partia\r\n  l\r\n");
        _ = await Assert.That(error).Contains("  bad[2J    request");
        _ = await Assert.That(error).DoesNotContain("\u001b[2J");
    }

    private static async Task<(bool Completed, string Output, string Error)> Render(
        IEnumerable<Event> events,
        CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        foreach (var published in events)
        {
            await stream.WriteAsync(published, cancellationToken);
        }

        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        using var driver = new CliLifecycleDriver(enhanced: true);
        var terminal = new TestTerminal(driver.Input, output, error, 8);
        var completed = await new EnhancedCli(
            new GeneratedParrot.ParrotClient(driver.Invoker),
            driver.Interrupts,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            new UnusedCredentials(),
            new OpenAiOAuthClient(driver.Http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml")),
            ["provider"],
            terminal,
            Presenters(),
            ImmediateDelay()).RenderTurn(stream.Reader, cancellationToken);
        return (completed, output.ToString(), error.ToString());
    }

    private static Func<TimeSpan, CancellationToken, Task> ImmediateDelay() =>
        static (_, cancellationToken) => Task.Delay(1, cancellationToken);

    private static ToolPresenterRegistry Presenters() => new([], new GenericToolPresenter());

    private static async Task OutputContains(
        ScriptedTerminal terminal, string text, CancellationToken cancellationToken)
    {
        while (!(terminal.Output.ToString() ?? string.Empty).Contains(text, StringComparison.Ordinal))
        {
            await Task.Delay(5, cancellationToken);
        }
    }

    private static async Task Sent(ScriptedInvoker invoker, int count, CancellationToken cancellationToken)
    {
        while (invoker.Sent.Count < count)
        {
            await Task.Delay(5, cancellationToken);
        }
    }

    private static bool UntrustedEscape(string output)
    {
        var withoutOwnedAnsi = Regex.Replace(
            output,
            "\\x1b(?:\\[\\?25[lh]|\\[2K|\\[[0-9]+A|\\[(?:0|2|31|32|36)m)",
            string.Empty,
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return withoutOwnedAnsi.Contains('\u001b', StringComparison.Ordinal);
    }

    private static int Count(string value, string part)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(part, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += part.Length;
        }

        return count;
    }

    private sealed class ControlledSubmitDelay
    {
        private readonly Channel<PendingDelay> _pending = Channel.CreateUnbounded<PendingDelay>();

        public Task Wait(TimeSpan interval, CancellationToken cancellationToken)
        {
            var pending = new PendingDelay(interval, cancellationToken);
            if (!_pending.Writer.TryWrite(pending))
            {
                throw new InvalidOperationException("unable to queue the submit delay");
            }

            return pending.Wait();
        }

        public ValueTask<PendingDelay> Read(CancellationToken cancellationToken) =>
            _pending.Reader.ReadAsync(cancellationToken);
    }

    private sealed class PendingDelay(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Canceled { get; } = ObserveCancellation(cancellationToken);

        public TimeSpan Interval { get; } = interval;

        public void Release() => _released.TrySetResult();

        public Task Wait() => _released.Task.WaitAsync(cancellationToken);

        private static async Task ObserveCancellation(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }
}
