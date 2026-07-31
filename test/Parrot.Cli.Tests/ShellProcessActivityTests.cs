using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class ShellProcessActivityTests
{
    private static readonly LiveBufferRenderContext Context = new(160, new TerminalPalette(false));

    [Test]
    public async Task Typed_yield_survives_tool_completion_and_newer_absent_snapshot_prevents_resurrection(
        CancellationToken cancellationToken)
    {
        var draws = new List<string>();
        var commits = new List<string>();
        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            draws.Add(Render(items));
            return Task.CompletedTask;
        }

        Task Commit(IScrollbackItem scrollback, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            commits.Add(string.Join('|', scrollback.Render(new ScrollbackRenderContext(160, Context.Palette))));
            draws.Add(Render(items));
            return Task.CompletedTask;
        }

        using var view = new RawActivityView(
            Draw,
            Commit,
            new ToolPresenterRegistry([new ExecCommandToolPresenter()], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);

        await view.Render(
            new Event { AgentSessionId = "main", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "main",
                ToolCallChunk = new ToolCallChunk
                {
                    ToolCallId = "call",
                    ToolName = "exec_command",
                    ArgumentsFragment = "{\"command\":\"sleep 20\"}",
                },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "main",
                ToolStarted = new ToolStarted { ToolCallId = "call", ToolName = "exec_command" },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "main",
                ToolFinished = new ToolFinished
                {
                    ToolCallId = "call",
                    ToolName = "exec_command",
                    Result = "not an identity",
                    YieldedProcess = new YieldedShellProcess
                    {
                        ProcessId = "opaque-1",
                        Name = "build",
                        InventoryInstanceId = "inventory",
                        VisibleRevision = 1,
                    },
                },
            },
            cancellationToken);

        _ = await Assert.That(draws[^1]).Contains("$ sleep 20 (process build running 0s)");
        _ = await Assert.That(commits[^1]).Contains("process build running");

        await view.Render(
            new Event
            {
                AgentSessionId = "main",
                ToolFinished = new ToolFinished
                {
                    ToolCallId = "wait-call",
                    ToolName = "wait_process",
                    YieldedProcess = new YieldedShellProcess
                    {
                        ProcessId = "opaque-1",
                        Name = "build",
                        InventoryInstanceId = "inventory",
                        VisibleRevision = 1,
                    },
                },
            },
            cancellationToken);
        _ = await Assert.That(draws[^1]).Contains("$ sleep 20 (process build running 0s)");
        _ = await Assert.That(draws[^1]).DoesNotContain("$  (process build running");

        await view.ReplaceContent([new LiveTextValue("answer")], cancellationToken);
        _ = await Assert.That(draws[^1]).Contains("answer");
        _ = await Assert.That(draws[^1]).Contains("process build running");

        await view.ReplaceProcesses(Snapshot("inventory", 2), cancellationToken);
        _ = await Assert.That(draws[^1]).DoesNotContain("process build running");

        await view.Render(
            new Event
            {
                AgentSessionId = "main",
                ToolFinished = new ToolFinished
                {
                    ToolCallId = "call",
                    ToolName = "exec_command",
                    YieldedProcess = new YieldedShellProcess
                    {
                        ProcessId = "opaque-1",
                        Name = "build",
                        InventoryInstanceId = "inventory",
                        VisibleRevision = 1,
                    },
                },
            },
            cancellationToken);
        _ = await Assert.That(draws[^1]).DoesNotContain("process build running");
    }

    [Test]
    public async Task Authoritative_snapshot_replaces_processes_by_opaque_id(CancellationToken cancellationToken)
    {
        var draws = new List<string>();
        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            draws.Add(Render(items));
            return Task.CompletedTask;
        }

        using var view = new RawActivityView(
            Draw,
            static (_, _, _) => Task.CompletedTask,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        var first = Process("opaque-1", "same", "first command", 5000);
        await view.ReplaceProcesses(Snapshot("inventory", 1, first), cancellationToken);
        _ = await Assert.That(draws[^1]).Contains("first command");
        _ = await Assert.That(draws[^1]).Contains("running 5s");

        var replacement = Process("opaque-2", "same", "replacement command", 0);
        await view.ReplaceProcesses(Snapshot("inventory", 2, replacement), cancellationToken);
        _ = await Assert.That(draws[^1]).Contains("replacement command");
        _ = await Assert.That(draws[^1]).DoesNotContain("first command");

        await view.ReplaceProcesses(Snapshot("inventory", 1, first), cancellationToken);
        _ = await Assert.That(draws[^1]).Contains("replacement command");
        _ = await Assert.That(draws[^1]).DoesNotContain("first command");

        await view.ReplaceProcesses(Snapshot("replacement-inventory", 1), cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "main",
                ToolFinished = new ToolFinished
                {
                    ToolCallId = "retired",
                    ToolName = "exec_command",
                    YieldedProcess = new YieldedShellProcess
                    {
                        ProcessId = "delayed",
                        Name = "delayed",
                        InventoryInstanceId = "inventory",
                        VisibleRevision = 3,
                    },
                },
            },
            cancellationToken);
        _ = await Assert.That(draws[^1]).DoesNotContain("delayed");
    }

    private static string Render(IReadOnlyList<ILiveBufferItem> items) =>
        string.Join('|', items.SelectMany(item => item.Render(Context).Lines).Select(static line => line.Text));

    private static ActiveShellProcess Process(string id, string name, string command, long elapsedMs) => new()
    {
        ProcessId = id,
        Name = name,
        Command = command,
        OwnerAgentSessionId = "main",
        OwnerAgentName = "main",
        ElapsedMs = elapsedMs,
    };

    private static ShellProcessSnapshot Snapshot(
        string instanceId,
        ulong revision,
        params ActiveShellProcess[] processes)
    {
        var snapshot = new ShellProcessSnapshot
        {
            InventoryInstanceId = instanceId,
            Revision = revision,
            ChunkCount = 1,
        };
        snapshot.Processes.AddRange(processes);
        return snapshot;
    }
}
