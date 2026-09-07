using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class ShellProcessActivityTests
{
    private static readonly LiveBufferRenderContext Context = new(160, new TerminalPalette(false));

    [Test]
    public async Task Yielded_exec_remains_live_without_committing(CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();

        await activity.Yield("call", "sleep 20", "process", "process-1", "inventory", 1, cancellationToken);

        _ = await Assert.That(activity.Commits).IsEmpty();
        _ = await Assert.That(activity.Draws[^1]).Contains("$ sleep 20 (process process running");
    }

    [Test]
    public async Task Origin_tool_completion_waits_for_terminal_output(CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();
        await activity.Start("call", "rg AgentTask", cancellationToken);
        var process = new ActiveShellProcess { ProcessId = "process-1", Name = "build", Command = "rg AgentTask", OwnerAgentSessionId = "main", OwnerAgentName = "main", OriginToolCallId = "call" };

        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 1, ChunkCount = 1, Processes = { process } }, cancellationToken);
        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 2, ChunkCount = 1 }, cancellationToken);

        _ = await Assert.That(activity.Commits).IsEmpty();

        await activity.FinishSuccessfully(
            "call",
            "Process exited with code 0 after 0.02s\nmatching output",
            cancellationToken);

        _ = await Assert.That(activity.Commits).Count().IsEqualTo(1);
        _ = await Assert.That(activity.Commits[0]).IsEqualTo(
            "✓ $ rg AgentTask|Process exited with code 0 after 0.02s|matching output");
    }

    [Test]
    public async Task Terminal_output_suppresses_later_origin_process_completion(CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();
        await activity.Start("call", "rg AgentTask", cancellationToken);
        var process = new ActiveShellProcess { ProcessId = "process-1", Name = "build", Command = "rg AgentTask", OwnerAgentSessionId = "main", OwnerAgentName = "main", OriginToolCallId = "call" };

        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 1, ChunkCount = 1, Processes = { process } }, cancellationToken);
        await activity.FinishSuccessfully(
            "call",
            "Process exited with code 0 after 0.02s\nmatching output",
            cancellationToken);
        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 2, ChunkCount = 1 }, cancellationToken);

        _ = await Assert.That(activity.Commits).Count().IsEqualTo(1);
        _ = await Assert.That(activity.Commits[0]).Contains("matching output");
    }

    [Test]
    public async Task Newer_omission_commits_neutral_original_command(CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();
        await activity.Yield("call", "sleep 20", "build", "process-1", "inventory", 1, cancellationToken);

        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 2, ChunkCount = 1 }, cancellationToken);

        _ = await Assert.That(activity.Commits).Count().IsEqualTo(1);
        _ = await Assert.That(activity.Commits[0]).IsEqualTo("$ sleep 20");
        _ = await Assert.That(activity.Commits[0]).DoesNotContain("process build running");
        _ = await Assert.That(activity.Draws[^1]).DoesNotContain("process build running");
    }

    [Test]
    public async Task Completed_process_flushes_the_entire_multiline_command(CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();
        var command = string.Join("\\n", Enumerable.Range(1, 12).Select(static line => $"echo line-{line}"));
        await activity.Yield("call", command, "build", "process-1", "inventory", 1, cancellationToken);

        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 2, ChunkCount = 1 }, cancellationToken);

        _ = await Assert.That(activity.Commits).HasSingleItem();
        _ = await Assert.That(activity.Commits[0]).Contains("$ echo line-1|echo line-2");
        _ = await Assert.That(activity.Commits[0]).EndsWith("echo line-12");
    }

    [Test]
    public async Task Snapshot_before_yielded_completion_commits_without_showing_process(CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();
        await activity.Start("call", "sleep 20", cancellationToken);
        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 2, ChunkCount = 1 }, cancellationToken);

        await activity.Finish("call", "build", "process-1", "inventory", 1, cancellationToken);

        _ = await Assert.That(activity.Commits).Count().IsEqualTo(1);
        _ = await Assert.That(activity.Commits[0]).IsEqualTo("$ sleep 20");
        _ = await Assert.That(activity.Draws.Any(static draw =>
            draw.Contains("process build running", StringComparison.Ordinal))).IsFalse();

        await activity.Finish("call", "build", "process-1", "inventory", 1, cancellationToken);
        _ = await Assert.That(activity.Commits).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Completion_tombstone_correlates_after_yield(CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();
        await activity.Yield("call", "sleep 20", "build", "process-1", "inventory", 1, cancellationToken);

        await activity.Replace(
            new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 2, ChunkCount = 1, CompletedProcesses = { new CompletionFixture("process-1", 7_123).Completion } },
            cancellationToken);

        _ = await Assert.That(activity.Commits).HasSingleItem();
        _ = await Assert.That(activity.Commits[0]).IsEqualTo("$ sleep 20 (7s)");
    }

    [Test]
    public async Task Retained_completion_tombstone_flushes_a_command_once(CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();
        await activity.Yield("call", "sleep 20", "build", "process-1", "inventory", 1, cancellationToken);

        await activity.Replace(
            new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 2, ChunkCount = 1, CompletedProcesses = { new CompletionFixture("process-1", 7_123).Completion } },
            cancellationToken);
        await activity.Replace(
            new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 3, ChunkCount = 1, CompletedProcesses = { new CompletionFixture("process-1", 7_123).Completion } },
            cancellationToken);

        _ = await Assert.That(activity.Commits).HasSingleItem();
        _ = await Assert.That(activity.Commits[0]).IsEqualTo("$ sleep 20 (7s)");
    }

    [Test]
    [Arguments(null, "$ sleep 20")]
    [Arguments(4_999L, "$ sleep 20")]
    [Arguments(5_000L, "$ sleep 20")]
    [Arguments(5_001L, "$ sleep 20 (5s)")]
    [Arguments(65_123L, "$ sleep 20 (1m 05s)")]
    [Arguments(3_723_000L, "$ sleep 20 (1h 02m 03s)")]
    public async Task Completion_tombstone_renders_duration_only_above_the_strict_threshold(
        long? elapsedMilliseconds,
        string expected,
        CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();
        await activity.Yield("call", "sleep 20", "build", "process-1", "inventory", 1, cancellationToken);

        await activity.Replace(
            new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 2, ChunkCount = 1, CompletedProcesses = { new CompletionFixture("process-1", elapsedMilliseconds).Completion } },
            cancellationToken);

        _ = await Assert.That(activity.Commits).HasSingleItem();
        _ = await Assert.That(activity.Commits[0]).IsEqualTo(expected);
    }

    [Test]
    public async Task Completion_tombstone_correlates_before_yield_without_replaying_history(
        CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();
        await activity.Replace(
            new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 2, ChunkCount = 1, CompletedProcesses = { new CompletionFixture("process-1", 8_456).Completion, new CompletionFixture("historical", 9_000).Completion } },
            cancellationToken);

        _ = await Assert.That(activity.Commits).IsEmpty();

        await activity.Yield("call", "sleep 20", "build", "process-1", "inventory", 1, cancellationToken);
        await activity.Finish("call", "build", "process-1", "inventory", 1, cancellationToken);

        _ = await Assert.That(activity.Commits).HasSingleItem();
        _ = await Assert.That(activity.Commits[0]).IsEqualTo("$ sleep 20 (8s)");
    }

    [Test]
    public async Task Equal_and_stale_snapshots_do_not_complete_yielded_process(CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();
        await activity.Yield("call", "sleep 20", "build", "process-1", "inventory", 2, cancellationToken);

        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 2, ChunkCount = 1 }, cancellationToken);
        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 1, ChunkCount = 1 }, cancellationToken);

        _ = await Assert.That(activity.Commits).IsEmpty();
        _ = await Assert.That(activity.Draws[^1]).Contains("$ sleep 20 (process build running");
    }

    [Test]
    public async Task Reyielded_process_retains_its_original_command(CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();
        await activity.Yield("first", "first command", "build", "process-1", "inventory", 1, cancellationToken);
        await activity.Yield("second", "replacement command", "build", "process-1", "inventory", 2, cancellationToken);

        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 3, ChunkCount = 1 }, cancellationToken);

        _ = await Assert.That(activity.Commits).Count().IsEqualTo(1);
        _ = await Assert.That(activity.Commits[0]).Contains("first command");
        _ = await Assert.That(activity.Commits[0]).DoesNotContain("replacement command");
    }

    [Test]
    public async Task Reconnected_inventory_omission_completes_existing_process(CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();
        await activity.Yield("call", "sleep 20", "build", "process-1", "inventory-a", 1, cancellationToken);

        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory-b", Revision = 1, ChunkCount = 1, Processes = { new ActiveShellProcess { ProcessId = "process-1", Name = "build", Command = "reported command", OwnerAgentSessionId = "main", OwnerAgentName = "main" } } }, cancellationToken);
        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory-b", Revision = 2, ChunkCount = 1 }, cancellationToken);

        _ = await Assert.That(activity.Commits).Count().IsEqualTo(1);
        _ = await Assert.That(activity.Commits[0]).Contains("reported command");
        _ = await Assert.That(activity.Commits[0]).DoesNotContain("sleep 20");
    }

    [Test]
    public async Task Snapshot_origin_completion_uses_reported_owner_hierarchy(CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();

        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 1, ChunkCount = 1, Processes = { new ActiveShellProcess { ProcessId = "process-1", Name = "build", Command = "reported command", OwnerAgentSessionId = "opaque-child-id", OwnerAgentName = "worker", ParentAgentSessionId = "opaque-parent-id", ParentAgentName = "parent", Depth = 2 } } }, cancellationToken);
        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 2, ChunkCount = 1 }, cancellationToken);

        _ = await Assert.That(activity.Commits).Count().IsEqualTo(1);
        _ = await Assert.That(activity.Commits[0].StartsWith(
            "    $ [worker] reported command",
            StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Inventory_replacement_does_not_complete_missing_old_process(CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();
        await activity.Yield("call", "sleep 20", "build", "process-1", "inventory-a", 1, cancellationToken);

        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory-b", Revision = 1, ChunkCount = 1 }, cancellationToken);

        _ = await Assert.That(activity.Commits).IsEmpty();
    }

    [Test]
    public async Task Duplicate_omissions_commit_process_once(CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();
        await activity.Yield("call", "sleep 20", "build", "process-1", "inventory", 1, cancellationToken);

        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 2, ChunkCount = 1 }, cancellationToken);
        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 3, ChunkCount = 1 }, cancellationToken);

        _ = await Assert.That(activity.Commits).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Multiple_omissions_commit_in_process_id_order(CancellationToken cancellationToken)
    {
        await using var activity = new ProcessActivity();
        await activity.Yield("second", "second command", "second", "process-b", "inventory", 1, cancellationToken);
        await activity.Yield("first", "first command", "first", "process-a", "inventory", 1, cancellationToken);

        await activity.Replace(new ShellProcessSnapshot { InventoryInstanceId = "inventory", Revision = 2, ChunkCount = 1 }, cancellationToken);

        _ = await Assert.That(activity.Commits).Count().IsEqualTo(2);
        _ = await Assert.That(activity.Commits[0]).Contains("first command");
        _ = await Assert.That(activity.Commits[1]).Contains("second command");
    }

    private static string Render(IReadOnlyList<ILiveBufferItem> items) =>
        string.Join('|', items.SelectMany(item => item.Render(Context).Lines).Select(static line => line.Text));

    private sealed class CompletionFixture
    {
        public CompletionFixture(string processId, long? elapsedMilliseconds)
        {
            Completion = new CompletedShellProcess { ProcessId = processId };
            if (elapsedMilliseconds is { } value)
            {
                Completion.ElapsedMs = value;
            }
        }

        public CompletedShellProcess Completion { get; }
    }

    private sealed class ProcessActivity : IAsyncDisposable
    {
        private readonly RawActivityView _view;
        private bool _turnStarted;

        public ProcessActivity()
        {
            _view = new RawActivityView(
                Draw,
                Commit,
                new ToolPresenterRegistry([new ExecCommandToolPresenter(TimeProvider.System, [])], new GenericToolPresenter()),
                static (_, _) => Task.CompletedTask);
        }

        public List<string> Draws { get; } = [];

        public List<string> Commits { get; } = [];

        public ValueTask DisposeAsync() => _view.DisposeAsync();

        public async Task Yield(
            string callId,
            string command,
            string name,
            string processId,
            string inventoryId,
            ulong revision,
            CancellationToken cancellationToken)
        {
            await Start(callId, command, cancellationToken);
            await Finish(callId, name, processId, inventoryId, revision, cancellationToken);
        }

        public async Task Start(string callId, string command, CancellationToken cancellationToken)
        {
            if (!_turnStarted)
            {
                _turnStarted = true;
                await _view.Render(
                    new Event
                    {
                        AgentSessionId = "main",
                        TurnStarted = new TurnStarted(),
                    },
                    cancellationToken);
            }

            await _view.Render(
                new Event
                {
                    AgentSessionId = "main",
                    ToolCallChunk = new ToolCallChunk
                    {
                        ToolCallId = callId,
                        ToolName = "exec_command",
                        ArgumentsFragment = $"{{\"command\":\"{command}\"}}",
                    },
                },
                cancellationToken);
            await _view.Render(
                new Event
                {
                    AgentSessionId = "main",
                    ToolStarted = new ToolStarted { ToolCallId = callId, ToolName = "exec_command" },
                },
                cancellationToken);
        }

        public Task Finish(
            string callId,
            string name,
            string processId,
            string inventoryId,
            ulong revision,
            CancellationToken cancellationToken) =>
            _view.Render(
                new Event
                {
                    AgentSessionId = "main",
                    ToolFinished = new ToolFinished
                    {
                        ToolCallId = callId,
                        ToolName = "exec_command",
                        YieldedProcess = new YieldedShellProcess
                        {
                            ProcessId = processId,
                            Name = name,
                            InventoryInstanceId = inventoryId,
                            VisibleRevision = revision,
                        },
                    },
                },
                cancellationToken);

        public Task FinishSuccessfully(string callId, string result, CancellationToken cancellationToken) =>
            _view.Render(
                new Event
                {
                    AgentSessionId = "main",
                    ToolFinished = new ToolFinished
                    {
                        ToolCallId = callId,
                        ToolName = "exec_command",
                        Result = result,
                    },
                },
                cancellationToken);

        public Task Replace(ShellProcessSnapshot snapshot, CancellationToken cancellationToken) =>
            _view.ReplaceProcesses(snapshot, cancellationToken);

        private Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Draws.Add(Render(items));
            return Task.CompletedTask;
        }

        private Task Commit(
            IScrollbackItem scrollback,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commits.Add(string.Join('|', scrollback.Render(new ScrollbackRenderContext(160, Context.Palette))));
            Draws.Add(Render(items));
            return Task.CompletedTask;
        }
    }
}
