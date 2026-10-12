using Grpc.Core;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Core.Tests;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class AssistantTextFlushTests
{
    [Test]
    [Arguments("root")]
    [Arguments("child")]
    public async Task Every_selected_boundary_commits_its_owners_buffer_before_presentation(
        string owner, CancellationToken cancellationToken)
    {
        foreach (var boundary in Boundaries(owner))
        {
            await using var fixture = new FlushFixture();
            await fixture.Start(cancellationToken);
            await fixture.Coordinator.Prepare(boundary, cancellationToken);
            _ = await Assert.That(fixture.Text).IsEmpty();
            var text = $"buffered {boundary.PayloadCase}";
            await fixture.Buffer(owner, text, cancellationToken);
            _ = await Assert.That(fixture.Text).DoesNotContain(text);

            await fixture.Coordinator.Prepare(boundary, cancellationToken);
            _ = await Assert.That(fixture.Text).Contains(text);
            _ = await Assert.That(fixture.Text.AsSpan().Count(text)).IsEqualTo(1);

            await fixture.Coordinator.Prepare(boundary, cancellationToken);
            _ = await Assert.That(fixture.Text.AsSpan().Count(text)).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments("root")]
    [Arguments("child")]
    public async Task Consecutive_text_and_excluded_events_leave_response_buffered(
        string owner, CancellationToken cancellationToken)
    {
        await using var fixture = new FlushFixture();
        await fixture.Start(cancellationToken);
        await fixture.Buffer(owner, "first ", cancellationToken);
        await fixture.Buffer(owner, "second", cancellationToken);
        foreach (var excluded in NonBoundaries(owner))
        {
            await fixture.Coordinator.Prepare(excluded, cancellationToken);
            _ = await Assert.That(fixture.Text).DoesNotContain("first second");
        }

        byte[] unknownPayload = [0xba, 0x3e, 0x00];
        var future = Event.Parser.ParseFrom(unknownPayload);
        future.AgentSessionId = owner;
        await fixture.Coordinator.Prepare(future, cancellationToken);
        _ = await Assert.That(fixture.Text).DoesNotContain("first second");
        await fixture.Coordinator.Prepare(new Event { AgentSessionId = owner, TurnEnded = new TurnEnded() }, cancellationToken);
        _ = await Assert.That(fixture.Text).Contains("first second");
        _ = await Assert.That(fixture.Text.AsSpan().Count("first second")).IsEqualTo(1);
    }

    [Test]
    public async Task Interleaved_hierarchy_flushes_only_exact_event_owner(CancellationToken cancellationToken)
    {
        await using var fixture = new FlushFixture();
        await fixture.Start(cancellationToken);
        string[] owners = ["root", "child", "sibling", "grandchild"];
        foreach (var owner in owners)
        {
            await fixture.Buffer(owner, $"response from [{owner}]", cancellationToken);
        }

        await fixture.Coordinator.Prepare(new Event { ReasoningChunk = new ReasoningChunk() }, cancellationToken);
        await fixture.Coordinator.Prepare(new Event { AgentSessionId = "unknown", TurnEnded = new TurnEnded() }, cancellationToken);
        await fixture.Coordinator.Prepare(new Event { AgentSessionId = "root", ToolSnapshot = new ToolSnapshot() }, cancellationToken);
        _ = await Assert.That(fixture.Text).IsEmpty();

        string[] order = ["grandchild", "sibling", "child", "root"];
        for (var index = 0; index < order.Length; index++)
        {
            var owner = order[index];
            await fixture.Coordinator.Prepare(
                new Event
                {
                    AgentSessionId = "unrelated-envelope-owner",
                    ToolSnapshot = new ToolSnapshot { OwnerAgentSessionId = owner },
                },
                cancellationToken);
            foreach (var flushed in order.Take(index + 1))
            {
                _ = await Assert.That(fixture.Text.AsSpan().Count($"response from [{flushed}]")).IsEqualTo(1);
            }

            foreach (var pending in order.Skip(index + 1))
            {
                _ = await Assert.That(fixture.Text).DoesNotContain($"response from [{pending}]");
            }
        }
    }

    [Test]
    public async Task Child_lifecycle_description_does_not_flush_parent(CancellationToken cancellationToken)
    {
        await using var fixture = new FlushFixture();
        await fixture.Start(cancellationToken);
        await fixture.Buffer("root", "parent pending", cancellationToken);
        await fixture.Buffer("child", "child pending", cancellationToken);
        await fixture.Coordinator.Prepare(
            new Event
            {
                AgentSessionId = "child",
                AgentFinished = new AgentFinished { ParentAgentSessionId = "root" },
            },
            cancellationToken);
        _ = await Assert.That(fixture.Text).Contains("child pending");
        _ = await Assert.That(fixture.Text).DoesNotContain("parent pending");
    }

    [Test]
    [Arguments("root", "encrypted")]
    [Arguments("child", "encrypted")]
    [Arguments("root", "summary")]
    [Arguments("child", "summary")]
    [Arguments("root", "raw")]
    [Arguments("child", "raw")]
    public async Task Reasoning_boundary_commits_response_before_summary_or_raw_notice(
        string owner, string kind, CancellationToken cancellationToken)
    {
        var committed = new List<string>();
        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            committed.Add(string.Join('|', item.Render(new ScrollbackRenderContext(120, new TerminalPalette(false)))));
            return Task.CompletedTask;
        }

        await using var activity = new RawActivityView(
            static (_, _) => Task.CompletedTask,
            Commit,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        var foreground = new ForegroundTurn();
        var root = new EnhancedTurnView(static (_, _) => Task.CompletedTask, Commit, TextWriter.Null, static () => 120, false, foreground);
        var coordinator = new AssistantTextFlushCoordinator(activity.FlushResponse);
        coordinator.BindRoot(root.FlushResponse);
        foreach (var started in Starts())
        {
            foreground.Observe(started);
            await activity.Render(started, cancellationToken);
        }

        var text = new Event { AgentSessionId = owner, TextChunk = new TextChunk { Fragment = "answer before thought" } };
        _ = await root.Render(text, cancellationToken);
        await activity.Render(text, cancellationToken);
        var reasoning = new Event { AgentSessionId = owner };
        if (kind == "encrypted")
        {
            reasoning.EncryptedReasoningReceived = new EncryptedReasoningReceivedEvent();
        }
        else
        {
            reasoning.ReasoningChunk = new ReasoningChunk
            {
                Kind = kind == "summary" ? ReasoningKind.Summary : ReasoningKind.Raw,
                Fragment = "visible summary",
                Completed = true,
            };
        }

        await coordinator.Prepare(reasoning, cancellationToken);
        _ = await Assert.That(committed).HasSingleItem();
        _ = await Assert.That(committed[0]).Contains("answer before thought");
        await activity.Prepare(reasoning, cancellationToken);
        await activity.Render(reasoning, cancellationToken);
        _ = await Assert.That(committed).Count().IsEqualTo(2);
        _ = await Assert.That(committed[1]).Contains(kind switch
        {
            "encrypted" => "Reasoning received (encrypted)",
            "summary" => "visible summary",
            _ => "Reasoned for ",
        });
    }

    [Test]
    [Arguments("root")]
    [Arguments("child")]
    [Arguments("sibling")]
    [Arguments("grandchild")]
    public async Task Completion_and_cleanup_do_not_duplicate_earlier_commits(
        string owner, CancellationToken cancellationToken)
    {
        await using var fixture = new FlushFixture();
        await fixture.Start(cancellationToken);
        await fixture.Buffer(owner, "already committed", cancellationToken);
        await fixture.Coordinator.Prepare(new Event { AgentSessionId = owner, ToolCallChunk = new ToolCallChunk() }, cancellationToken);
        var ended = new Event { AgentSessionId = owner, TurnEnded = new TurnEnded() };
        await fixture.Coordinator.Prepare(ended, cancellationToken);
        await fixture.Activity.View.Prepare(ended, cancellationToken);
        await fixture.Activity.View.Render(ended, cancellationToken);
        _ = await fixture.Root.Render(ended, cancellationToken);
        await fixture.Root.End(cancellationToken);
        await fixture.Root.Cancel(cancellationToken);
        _ = await Assert.That(fixture.Text.AsSpan().Count("already committed")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Root_end_and_cancel_commit_pending_text_once(bool cancel, CancellationToken cancellationToken)
    {
        await using var fixture = new FlushFixture();
        await fixture.Start(cancellationToken);
        await fixture.Buffer("root", "cleanup pending", cancellationToken);
        if (cancel)
        {
            await fixture.Root.Cancel(cancellationToken);
        }
        else
        {
            await fixture.Root.End(cancellationToken);
        }

        await fixture.Root.End(cancellationToken);
        _ = await Assert.That(fixture.Text.AsSpan().Count("cleanup pending")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Snapshots_flush_before_replacement_even_when_partial_duplicate_or_stale(
        bool process, CancellationToken cancellationToken)
    {
        await using var fixture = new FlushFixture();
        await fixture.Start(cancellationToken);
        var source = new ChannelStreamWriter<Event>();
        var replacements = new List<string>();
        var observing = new AssistantTextFlushStreamReader(source.Reader, fixture.Coordinator);
        IAsyncStreamReader<Event> reader = process
            ? new ShellProcessSnapshotStreamReader(observing, (_, _, _) =>
            {
                replacements.Add(fixture.Text);
                return Task.CompletedTask;
            })
            : new QueueSnapshotStreamReader(observing, (_, _, _) =>
            {
                replacements.Add(fixture.Text);
                return Task.CompletedTask;
            });
        (ulong Revision, uint Chunk, bool Final)[] snapshots = [(2, 0, false), (2, 1, true), (2, 0, true), (1, 0, true)];
        for (var index = 0; index < snapshots.Length; index++)
        {
            var text = $"snapshot segment {index}";
            await fixture.Buffer("child", text, cancellationToken);
            var (revision, chunk, final) = snapshots[index];
            var inventory = new ToolSnapshot
            {
                OwnerAgentSessionId = "child",
                InventoryInstanceId = "inventory",
                Revision = revision,
                ChunkIndex = chunk,
                FinalChunk = final,
            };
            await source.WriteAsync(
                process
                    ? TestToolSnapshots.Process(inventory, new ShellProcessSnapshot())
                    : TestToolSnapshots.Queue(inventory, new QueueSnapshot()),
                cancellationToken);
            await source.WriteAsync(new Event { Id = "visible", TextChunk = new TextChunk() }, cancellationToken);
            _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
            _ = await Assert.That(reader.Current.Id).IsEqualTo("visible");
            _ = await Assert.That(fixture.Text.AsSpan().Count(text)).IsEqualTo(1);
            _ = await Assert.That(replacements.Count).IsEqualTo(index == 0 ? 0 : 1);
        }

        _ = await Assert.That(replacements[0]).Contains("snapshot segment 0");
        _ = await Assert.That(replacements[0]).Contains("snapshot segment 1");
    }

    [Test]
    public async Task Filtered_root_lifecycle_flushes_before_reader_returns_next_event(CancellationToken cancellationToken)
    {
        await using var fixture = new FlushFixture();
        await fixture.Start(cancellationToken);
        await fixture.Buffer("root", "filtered lifecycle response", cancellationToken);
        var source = new ChannelStreamWriter<Event>();
        var reader = new RootLifecycleFilterStreamReader(new AssistantTextFlushStreamReader(source.Reader, fixture.Coordinator));
        await source.WriteAsync(new Event { AgentSessionId = "root", AgentFinished = new AgentFinished() }, cancellationToken);
        await source.WriteAsync(new Event { Id = "visible", TextChunk = new TextChunk() }, cancellationToken);
        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(reader.Current.Id).IsEqualTo("visible");
        _ = await Assert.That(fixture.Text.AsSpan().Count("filtered lifecycle response")).IsEqualTo(1);
    }

    [Test]
    [Arguments("root")]
    [Arguments("child")]
    public async Task Failed_commit_restores_buffer_and_exact_owner_for_one_successful_retry(
        string owner, CancellationToken cancellationToken)
    {
        var committed = new List<string>();
        var fail = true;
        var palette = new TerminalPalette(false);
        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (fail)
            {
                fail = false;
                throw new IOException("injected commit failure");
            }

            committed.Add(string.Join('|', item.Render(new ScrollbackRenderContext(120, palette))));
            return Task.CompletedTask;
        }

        await using var activity = new RawActivityView(
            static (_, _) => Task.CompletedTask,
            Commit,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        var foreground = new ForegroundTurn();
        var root = new EnhancedTurnView(static (_, _) => Task.CompletedTask, Commit, TextWriter.Null, static () => 120, false, foreground);
        var coordinator = new AssistantTextFlushCoordinator(activity.FlushResponse);
        coordinator.BindRoot(root.FlushResponse);
        foreach (var started in Starts())
        {
            foreground.Observe(started);
            await activity.Render(started, cancellationToken);
        }

        var text = new Event { AgentSessionId = owner, TextChunk = new TextChunk { Fragment = "survives failure" } };
        _ = await root.Render(text, cancellationToken);
        await activity.Render(text, cancellationToken);
        var boundary = new Event { AgentSessionId = owner, ToolCallChunk = new ToolCallChunk() };
        _ = await Assert.That(() => coordinator.Prepare(boundary, cancellationToken)).Throws<IOException>();
        _ = await Assert.That(committed).IsEmpty();
        await coordinator.Prepare(new Event { AgentSessionId = owner == "root" ? "child" : "root", ToolCallChunk = new ToolCallChunk() }, cancellationToken);
        _ = await Assert.That(committed).IsEmpty();
        await coordinator.Prepare(boundary, cancellationToken);
        await coordinator.Prepare(boundary, cancellationToken);
        await root.End(cancellationToken);
        await activity.Render(new Event { AgentSessionId = owner, TurnEnded = new TurnEnded() }, cancellationToken);
        _ = await Assert.That(string.Join('|', committed).AsSpan().Count("survives failure")).IsEqualTo(1);
    }

    private static Event[] Boundaries(string owner) =>
    [
        new() { AgentSessionId = owner, ReasoningChunk = new ReasoningChunk() },
        new() { AgentSessionId = owner, EncryptedReasoningReceived = new EncryptedReasoningReceivedEvent() },
        new() { AgentSessionId = owner, ToolCallChunk = new ToolCallChunk() },
        new() { AgentSessionId = owner, ToolRequestReceived = new ToolRequestReceived() },
        new() { AgentSessionId = owner, ToolExecutionStarted = new ToolExecutionStarted() },
        new() { AgentSessionId = owner, ToolStarted = new ToolStarted() },
        new() { AgentSessionId = owner, ToolFinished = new ToolFinished() },
        new() { AgentSessionId = owner, ToolCancelled = new ToolCancelled() },
        new() { AgentSessionId = owner, ToolError = new ToolError() },
        new() { ToolSnapshot = new ToolSnapshot { OwnerAgentSessionId = owner } },
        new() { AgentSessionId = owner, TurnStarted = new TurnStarted() },
        new() { AgentSessionId = owner, TurnEnded = new TurnEnded() },
        new() { AgentSessionId = owner, TurnFailed = new TurnFailed() },
        new() { AgentSessionId = owner, AgentStarted = new AgentStarted() },
        new() { AgentSessionId = owner, AgentFinished = new AgentFinished() },
        new() { AgentSessionId = owner, AgentFailed = new AgentFailed() },
        new() { AgentSessionId = owner, ModeTurnCompleted = new ModeTurnCompleted() },
        new() { AgentSessionId = owner, PlanCompleted = new PlanCompleted() },
    ];

    private static Event[] NonBoundaries(string owner) =>
    [
        new() { AgentSessionId = owner, TextChunk = new TextChunk() },
        new() { AgentSessionId = owner, AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent() },
        new() { AgentSessionId = owner, ProviderCallUsage = new ProviderCallUsage() },
        new() { AgentSessionId = owner, RequestUsageRecorded = new RequestUsageRecorded() },
        new() { AgentSessionId = owner, SessionUsageSnapshot = new SessionUsageSnapshot() },
        new() { AgentSessionId = owner, ProviderRequestPhaseChanged = new ProviderRequestPhaseChangedEvent() },
        new() { AgentSessionId = owner, RetryNotice = new RetryNotice() },
        new() { AgentSessionId = owner, AgentNotice = new AgentNotice() },
        new() { AgentSessionId = owner, CompactionStarted = new CompactionStarted() },
        new() { AgentSessionId = owner, CompactionFinished = new CompactionFinished() },
        new() { AgentSessionId = owner, CompactionFailed = new CompactionFailed() },
        new() { AgentSessionId = owner, InputAdmitted = new InputAdmitted() },
        new() { AgentSessionId = owner, InputPromoted = new InputPromoted() },
        new() { AgentSessionId = owner, InputCanceled = new InputCanceled() },
        new() { AgentSessionId = owner, PermissionPending = new PendingPermission() },
        new() { AgentSessionId = owner, SkillLoaded = new SkillLoadedEvent() },
        new() { AgentSessionId = owner },
        new() { SessionUsageSnapshot = new SessionUsageSnapshot() },
    ];

    private static Event[] Starts() =>
    [
        new() { AgentSessionId = "root", TurnStarted = new TurnStarted() },
        new() { AgentSessionId = "child", AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "worker" } },
        new() { AgentSessionId = "child", TurnStarted = new TurnStarted() },
        new() { AgentSessionId = "sibling", AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "sibling" } },
        new() { AgentSessionId = "sibling", TurnStarted = new TurnStarted() },
        new() { AgentSessionId = "grandchild", AgentStarted = new AgentStarted { ParentAgentSessionId = "child", Name = "grandchild" } },
        new() { AgentSessionId = "grandchild", TurnStarted = new TurnStarted() },
    ];

    private sealed class FlushFixture : IAsyncDisposable
    {
        private readonly List<string> _rootCommitted = [];
        private readonly ForegroundTurn _foreground = new();

        public FlushFixture()
        {
            Root = new EnhancedTurnView(
                static (_, _) => Task.CompletedTask,
                (item, _, _) =>
                {
                    _rootCommitted.Add(string.Join('|', item.Render(new ScrollbackRenderContext(120, new TerminalPalette(false)))));
                    return Task.CompletedTask;
                },
                TextWriter.Null,
                static () => 120,
                false,
                _foreground);
            Coordinator = new AssistantTextFlushCoordinator(Activity.View.FlushResponse);
            Coordinator.BindRoot(Root.FlushResponse);
        }

        public RawActivityRecorder Activity { get; } = new(120, new ToolPresenterRegistry([], new GenericToolPresenter()), RawActivityRecorder.QuietPeriodDelay);

        public EnhancedTurnView Root { get; }

        public AssistantTextFlushCoordinator Coordinator { get; }

        public string Text => string.Join('|', _rootCommitted.Concat(Activity.Committed));

        public async Task Start(CancellationToken cancellationToken)
        {
            foreach (var started in Starts())
            {
                _foreground.Observe(started);
                await Activity.View.Render(started, cancellationToken);
            }

            Activity.Committed.Clear();
        }

        public async Task Buffer(string owner, string fragment, CancellationToken cancellationToken)
        {
            var text = new Event { AgentSessionId = owner, TextChunk = new TextChunk { Fragment = fragment } };
            await Coordinator.Prepare(text, cancellationToken);
            _ = await Root.Render(text, cancellationToken);
            await Activity.View.Prepare(text, cancellationToken);
            await Activity.View.Render(text, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Coordinator.UnbindRoot();
            return Activity.DisposeAsync();
        }
    }
}
