using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class AgentTaskVisibilityActivityTests
{
    [Test]
    [Arguments(AgentTaskProgressStatus.Running)]
    [Arguments(AgentTaskProgressStatus.Succeeded)]
    public async Task Hidden_tasks_follow_worker_activity_without_new_progress_revisions(
        AgentTaskProgressStatus status,
        CancellationToken cancellationToken)
    {
        await using var fixture = new ActivityFixture();
        await fixture.View.Render(new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } }, cancellationToken);
        await fixture.View.Render(new Event { AgentSessionId = "worker", AgentStarted = new AgentStarted { Name = "worker", ParentAgentSessionId = "root" } }, cancellationToken);
        var snapshot = new AgentTaskProgressSnapshot
        {
            Revision = 1,
            RootNodes = { new AgentTaskProgressNode { Name = "hidden task", Hidden = true, Status = status, AgentSessionId = "worker" } },
        };
        await fixture.View.Render(new Event { AgentSessionId = "root", AgentTaskProgressSnapshot = snapshot }, cancellationToken);
        _ = await Assert.That(fixture.Drawn).DoesNotContain("Agent tasks:");
        _ = await Assert.That(fixture.Drawn).DoesNotContain("hidden task");

        await fixture.View.Render(new Event { AgentSessionId = "worker", TurnStarted = new TurnStarted { Model = "model" } }, cancellationToken);
        _ = await Assert.That(fixture.Drawn).Contains("Agent tasks:");
        _ = await Assert.That(fixture.Drawn).Contains("hidden task");
        _ = await Assert.That(Count(fixture.Drawn, "[worker]")).IsEqualTo(1);

        await fixture.View.Render(
            new Event
            {
                AgentSessionId = "worker",
                ReasoningChunk = new ReasoningChunk { Kind = ReasoningKind.Raw, Fragment = "Still thinking" },
            },
            cancellationToken);
        _ = await Assert.That(fixture.Drawn).Contains("Thinking (");
        await fixture.View.Render(
            new Event
            {
                AgentSessionId = "worker",
                TextChunk = new TextChunk { Fragment = "streamed response" },
            },
            cancellationToken);
        _ = await Assert.That(fixture.Drawn).Contains("streamed response");
        await fixture.View.Render(new Event { AgentSessionId = "worker", TurnEnded = new TurnEnded { FinishReason = "stop" } }, cancellationToken);

        _ = await Assert.That(fixture.Drawn).DoesNotContain("Agent tasks:");
        _ = await Assert.That(fixture.Drawn).DoesNotContain("hidden task");
        _ = await Assert.That(fixture.Committed).Contains("streamed response");
        _ = await Assert.That(snapshot.RootNodes[0].Hidden).IsTrue();
        _ = await Assert.That(snapshot.Revision).IsEqualTo(1UL);
    }

    [Test]
    public async Task Nested_hidden_graphs_reveal_only_the_path_to_a_working_descendant(
        CancellationToken cancellationToken)
    {
        await using var fixture = new ActivityFixture();
        await fixture.View.Render(new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } }, cancellationToken);
        await fixture.View.Render(new Event { AgentSessionId = "parent", AgentStarted = new AgentStarted { Name = "parent", ParentAgentSessionId = "root" } }, cancellationToken);
        await fixture.View.Render(new Event { AgentSessionId = "worker", AgentStarted = new AgentStarted { Name = "worker", ParentAgentSessionId = "parent" } }, cancellationToken);
        await fixture.View.Render(
            new Event
            {
                AgentSessionId = "root",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    Revision = 1,
                    RootNodes = { new AgentTaskProgressNode { Name = "hidden parent", Hidden = true, AgentSessionId = "parent" } },
                },
            },
            cancellationToken);
        await fixture.View.Render(
            new Event
            {
                AgentSessionId = "parent",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    Revision = 1,
                    RootNodes =
                    {
                        new AgentTaskProgressNode { Name = "active child task", Hidden = true, AgentSessionId = "worker" },
                        new AgentTaskProgressNode { Name = "idle sibling", AgentSessionId = "idle" },
                    },
                },
            },
            cancellationToken);
        _ = await Assert.That(fixture.Drawn).DoesNotContain("Agent tasks:");

        await fixture.View.Render(new Event { AgentSessionId = "worker", TurnStarted = new TurnStarted { Model = "model" } }, cancellationToken);
        _ = await Assert.That(fixture.Drawn).Contains("hidden parent");
        _ = await Assert.That(fixture.Drawn).Contains("active child task");
        _ = await Assert.That(fixture.Drawn).DoesNotContain("idle sibling");
        _ = await Assert.That(Count(fixture.Drawn, "Agent tasks:")).IsEqualTo(1);
        _ = await Assert.That(Count(fixture.Drawn, "[worker]")).IsEqualTo(1);

        await fixture.View.Render(new Event { AgentSessionId = "worker", TurnEnded = new TurnEnded { FinishReason = "stop" } }, cancellationToken);
        _ = await Assert.That(fixture.Drawn).DoesNotContain("Agent tasks:");
    }

    [Test]
    public async Task Hidden_successful_graph_flush_does_not_commit_an_empty_task_block(
        CancellationToken cancellationToken)
    {
        await using var fixture = new ActivityFixture();
        await fixture.View.Render(new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } }, cancellationToken);
        await fixture.View.Render(
            new Event
            {
                AgentSessionId = "root",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    Revision = 1,
                    RootNodes = { new AgentTaskProgressNode { Name = "finished", Hidden = true, Status = AgentTaskProgressStatus.Succeeded } },
                },
            },
            cancellationToken);
        await fixture.View.Shutdown();

        _ = await Assert.That(fixture.Committed).IsEmpty();
        _ = await Assert.That(fixture.Drawn).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Flushed_terminal_tree_retains_hidden_descendants_for_later_activity(bool separateChildGraph, CancellationToken cancellationToken)
    {
        await using var fixture = new ActivityFixture();
        await fixture.View.Render(new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } }, cancellationToken);
        await fixture.View.Render(new Event { AgentSessionId = "worker", AgentStarted = new AgentStarted { Name = "worker", ParentAgentSessionId = "root" } }, cancellationToken);
        var descendant = new AgentTaskProgressNode { Name = "hidden descendant", Hidden = true, Status = AgentTaskProgressStatus.Succeeded, AgentSessionId = "worker" };
        var ancestor = new AgentTaskProgressNode { Name = "visible ancestor", AgentSessionId = separateChildGraph ? "ancestor" : string.Empty, Status = AgentTaskProgressStatus.Succeeded };
        if (separateChildGraph)
        {
            await fixture.View.Render(new Event { AgentSessionId = "ancestor", AgentStarted = new AgentStarted { Name = "ancestor", ParentAgentSessionId = "root" } }, cancellationToken);
            await fixture.View.Render(
                new Event
                {
                    AgentSessionId = "ancestor",
                    AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot { Revision = 1, RootNodes = { descendant } },
                },
                cancellationToken);
        }
        else
        {
            ancestor.Children.Add(descendant);
        }

        await fixture.View.Render(
            new Event
            {
                AgentSessionId = "root",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot { Revision = 1, RootNodes = { ancestor } },
            },
            cancellationToken);
        fixture.FlushProgress();
        await fixture.ProgressCommitted.Task.WaitAsync(cancellationToken);
        await fixture.View.Render(new Event { AgentSessionId = "worker", TurnStarted = new TurnStarted { Model = "model" } }, cancellationToken);
        _ = await Assert.That(fixture.Drawn).Contains("visible ancestor");
        _ = await Assert.That(fixture.Drawn).Contains("hidden descendant");
        await fixture.View.Render(new Event { AgentSessionId = "worker", TurnEnded = new TurnEnded { FinishReason = "stop" } }, cancellationToken);
        _ = await Assert.That(fixture.Drawn).DoesNotContain("hidden descendant");
    }

    [Test]
    public async Task Terminal_tree_kept_for_active_worker_retires_after_activity_stops(CancellationToken cancellationToken)
    {
        await using var fixture = new ActivityFixture();
        await fixture.View.Render(new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } }, cancellationToken);
        await fixture.View.Render(new Event { AgentSessionId = "worker", AgentStarted = new AgentStarted { Name = "worker", ParentAgentSessionId = "root" } }, cancellationToken);
        await fixture.View.Render(new Event { AgentSessionId = "worker", TurnStarted = new TurnStarted { Model = "model" } }, cancellationToken);
        await fixture.View.Render(
            new Event
            {
                AgentSessionId = "root",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    Revision = 1,
                    RootNodes = { new AgentTaskProgressNode { Name = "finished task", Status = AgentTaskProgressStatus.Succeeded, AgentSessionId = "worker" } },
                },
            },
            cancellationToken);
        fixture.FlushProgress();
        await fixture.ProgressCommitted.Task.WaitAsync(cancellationToken);
        await fixture.View.Render(new Event { AgentSessionId = "worker", TurnEnded = new TurnEnded { FinishReason = "stop" } }, cancellationToken);
        _ = await Assert.That(fixture.Drawn).DoesNotContain("Agent tasks:");
        _ = await Assert.That(fixture.Drawn).DoesNotContain("finished task");
        _ = await Assert.That(fixture.Committed).Contains("finished task");
    }

    private static int Count(string text, string fragment) =>
        text.Split(fragment, StringSplitOptions.None).Length - 1;

    private sealed class ActivityFixture : IAsyncDisposable
    {
        private readonly TerminalPalette _palette = new(false);
        private readonly List<string> _committed = [];
        private readonly TaskCompletionSource _progressFlush = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ActivityFixture() => View = new RawActivityView(
            Draw,
            Commit,
            static token => Task.Delay(Timeout.Infinite, token),
            (_, token) => _progressFlush.Task.WaitAsync(token),
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);

        public TaskCompletionSource ProgressCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RawActivityView View { get; }

        public string Drawn { get; private set; } = string.Empty;

        public string Committed => string.Join('|', _committed);

        public void FlushProgress() => _progressFlush.SetResult();

        public ValueTask DisposeAsync() => View.DisposeAsync();

        private Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Drawn = string.Join('|', items.SelectMany(item => item.Render(new LiveBufferRenderContext(120, _palette)).Lines)
                .Select(static line => line.Text));
            return Task.CompletedTask;
        }

        private Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken)
        {
            _committed.Add(string.Join('|', item.Render(new ScrollbackRenderContext(120, _palette))));
            _ = ProgressCommitted.TrySetResult();
            return Draw(items, cancellationToken);
        }
    }
}
