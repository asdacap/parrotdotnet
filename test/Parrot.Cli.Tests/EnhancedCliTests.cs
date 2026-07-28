using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Cli.Enhanced;
using Parrot.Config;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedCliTests
{
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
        _ = await Assert.That(output).Contains("Status prompt injected");
        _ = await Assert.That(output).Contains("  queued: next[2J    line");
        _ = await Assert.That(output).Contains("think]0;title\n");
        _ = await Assert.That(output).Contains("abcdefgh\r\n");
        _ = await Assert.That(output).Contains("ij\r\n");
        _ = await Assert.That(output).Contains("  tool call unrendered:");
        _ = await Assert.That(output).Contains("  * shell[31m started");
        _ = await Assert.That(output).Contains("  + shell[31m finished");
        _ = await Assert.That(output).Contains("  - read[2J cancelled");
        _ = await Assert.That(output).Contains("  ! write[31m: denied[2J");
        _ = await Assert.That(output).Contains("  * agent explorer[31m started");
        _ = await Assert.That(output).Contains("  + agent explorer[31m finished");
        _ = await Assert.That(output).Contains("  ! agent reviewer[31m: boom[2J");
        _ = await Assert.That(output).Contains("tail\r\n");
        _ = await Assert.That(output).Contains("  stop[2J - 3 in / 4 out");
        _ = await Assert.That(Count(output, "abcdefgh\r\n")).IsEqualTo(1);
        _ = await Assert.That(UntrustedEscape(output)).IsFalse();
        _ = await Assert.That(output).DoesNotContain("\u001b[?1049");
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
            new SlashCommandRegistry([new ExitCommand()]),
            driver.Interrupts,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            new UnusedCredentials(),
            new OpenAiOAuthClient(driver.Http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml")),
            ["provider"],
            terminal).RenderTurn(stream.Reader, cancellationToken);

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
            new SlashCommandRegistry([new ExitCommand()]),
            driver.Interrupts,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            new UnusedCredentials(),
            new OpenAiOAuthClient(driver.Http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml")),
            ["provider"],
            terminal).RenderTurn(stream.Reader, cancellationToken, BeforeRender);

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
        var renderer = new TerminalFrameRenderer(output, static () => 80, new TerminalPalette(false), 10, 12);
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
        using var view = new RawActivityView(Draw, Commit);

        await view.Render(
            new Event
            {
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
                ToolFinished = new ToolFinished { ToolCallId = "call-1", ToolName = "exec_command" },
            },
            cancellationToken);

        var rendered = output.ToString();
        var command = "tool call exec_command: {\"command\":\"dotnet test\"}";
        _ = await Assert.That(Count(rendered, "+ main: " + command + "\r\n")).IsEqualTo(1);
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

        using var view = new RawActivityView(Draw, Commit, Delay);
        using var animating = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var animation = view.Run(animating.Token);

        await view.Render(
            new Event { AgentSessionId = "main-session", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child-session",
                AgentStarted = new AgentStarted { Name = "explorer\u001b[31m" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child-session", TurnStarted = new TurnStarted { Model = "model" } },
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
        await view.DrawContent([new LiveTextValue("answer")], cancellationToken);

        var live = draws.Last();
        _ = await Assert.That(live).Contains("answer");
        _ = await Assert.That(live).Contains("⠋ agent main");
        _ = await Assert.That(live).Contains("⠋ agent explorer[31m");
        _ = await Assert.That(live).Contains("⠋ main: exec_command");
        _ = await Assert.That(live).Contains("⠋ explorer[31m: read[2J");

        await ticks.Writer.WriteAsync(true, cancellationToken);
        while (!draws.Any(value => value.Contains("⠙ agent main", StringComparison.Ordinal)))
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
        await view.Redraw(cancellationToken);
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
                AgentFinished = new AgentFinished { Name = "explorer" },
            },
            cancellationToken);

        await animating.CancelAsync();
        await animation;

        _ = await Assert.That(beforeAgentFinished).IsEqualTo(5);
        _ = await Assert.That(committed.Count).IsEqualTo(5);
        _ = await Assert.That(string.Join('|', committed)).Contains("+ main: tool call exec_command: dotnet test");
        _ = await Assert.That(string.Join('|', committed)).Contains("! explorer[31m: read[2J: denied[2J");
        _ = await Assert.That(string.Join('|', committed)).Contains("+ agent explorer[31m finished");
        _ = await Assert.That(draws.Last()).IsEmpty();
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
                    AgentStarted = new AgentStarted { Name = "explorer" },
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
        var mainContinued = output.IndexOf("main con", StringComparison.Ordinal);
        var mainEnded = output.IndexOf("main-stop", StringComparison.Ordinal);
        _ = await Assert.That(childEnded).IsGreaterThanOrEqualTo(0);
        _ = await Assert.That(mainContinued).IsGreaterThan(childEnded);
        _ = await Assert.That(mainEnded).IsGreaterThan(mainContinued);
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
        _ = await Assert.That(output).Contains("partial\r\n");
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
            new SlashCommandRegistry([new ExitCommand()]),
            driver.Interrupts,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            new UnusedCredentials(),
            new OpenAiOAuthClient(driver.Http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml")),
            ["provider"],
            terminal).RenderTurn(stream.Reader, cancellationToken);
        return (completed, output.ToString(), error.ToString());
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
}
