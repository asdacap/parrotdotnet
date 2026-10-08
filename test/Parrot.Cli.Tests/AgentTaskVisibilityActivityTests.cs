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
        var progressFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new RawActivityRecorder(
            120,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            (_, token) => progressFlush.Task.WaitAsync(token));
        await fixture.View.Render(new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } }, cancellationToken);
        await fixture.View.Render(new Event { AgentSessionId = "worker", AgentStarted = new AgentStarted { Name = "worker", ParentAgentSessionId = "root" } }, cancellationToken);
        var snapshot = new AgentTaskProgressSnapshot
        {
            Revision = 1,
            RootNodes = { new AgentTaskProgressNode { Name = "hidden task", Hidden = true, Status = status, AgentSessionId = "worker" } },
        };
        await fixture.View.Render(new Event { AgentSessionId = "root", AgentTaskProgressSnapshot = snapshot }, cancellationToken);
        _ = await Assert.That(fixture.LastDrawn).DoesNotContain("Agent tasks:");
        _ = await Assert.That(fixture.LastDrawn).DoesNotContain("hidden task");

        await fixture.View.Render(new Event { AgentSessionId = "worker", TurnStarted = new TurnStarted { Model = "model" } }, cancellationToken);
        _ = await Assert.That(fixture.LastDrawn).Contains("Agent tasks:");
        _ = await Assert.That(fixture.LastDrawn).Contains("hidden task");
        _ = await Assert.That(fixture.LastDrawn.AsSpan().Count("[worker]")).IsEqualTo(1);

        await fixture.View.Render(
            new Event
            {
                AgentSessionId = "worker",
                ReasoningChunk = new ReasoningChunk { Kind = ReasoningKind.Raw, Fragment = "Still thinking" },
            },
            cancellationToken);
        _ = await Assert.That(fixture.LastDrawn).Contains("Thinking (");
        await fixture.View.Render(
            new Event
            {
                AgentSessionId = "worker",
                TextChunk = new TextChunk { Fragment = "streamed response" },
            },
            cancellationToken);
        _ = await Assert.That(fixture.LastDrawn).Contains("streamed response");
        await fixture.View.Render(new Event { AgentSessionId = "worker", TurnEnded = new TurnEnded { FinishReason = "stop" } }, cancellationToken);

        _ = await Assert.That(fixture.LastDrawn).DoesNotContain("Agent tasks:");
        _ = await Assert.That(fixture.LastDrawn).DoesNotContain("hidden task");
        _ = await Assert.That(fixture.CommittedText).Contains("streamed response");
        _ = await Assert.That(snapshot.RootNodes[0].Hidden).IsTrue();
        _ = await Assert.That(snapshot.Revision).IsEqualTo(1UL);
    }

    [Test]
    public async Task Nested_hidden_graphs_reveal_only_the_path_to_a_working_descendant(
        CancellationToken cancellationToken)
    {
        var progressFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new RawActivityRecorder(
            120,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            (_, token) => progressFlush.Task.WaitAsync(token));
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
        _ = await Assert.That(fixture.LastDrawn).DoesNotContain("Agent tasks:");

        await fixture.View.Render(new Event { AgentSessionId = "worker", TurnStarted = new TurnStarted { Model = "model" } }, cancellationToken);
        _ = await Assert.That(fixture.LastDrawn).Contains("hidden parent");
        _ = await Assert.That(fixture.LastDrawn).Contains("active child task");
        _ = await Assert.That(fixture.LastDrawn).DoesNotContain("idle sibling");
        _ = await Assert.That(fixture.LastDrawn.AsSpan().Count("Agent tasks:")).IsEqualTo(1);
        _ = await Assert.That(fixture.LastDrawn.AsSpan().Count("[worker]")).IsEqualTo(1);

        await fixture.View.Render(new Event { AgentSessionId = "worker", TurnEnded = new TurnEnded { FinishReason = "stop" } }, cancellationToken);
        _ = await Assert.That(fixture.LastDrawn).DoesNotContain("Agent tasks:");
    }

    [Test]
    public async Task Hidden_successful_graph_flush_does_not_commit_an_empty_task_block(
        CancellationToken cancellationToken)
    {
        var progressFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new RawActivityRecorder(
            120,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            (_, token) => progressFlush.Task.WaitAsync(token));
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

        _ = await Assert.That(fixture.CommittedText).IsEmpty();
        _ = await Assert.That(fixture.LastDrawn).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Flushed_terminal_tree_retains_hidden_descendants_for_later_activity(bool separateChildGraph, CancellationToken cancellationToken)
    {
        var progressFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new RawActivityRecorder(
            120,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            (_, token) => progressFlush.Task.WaitAsync(token));
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
        progressFlush.SetResult();
        await fixture.CommitObserved.Task.WaitAsync(cancellationToken);
        await fixture.View.Render(new Event { AgentSessionId = "worker", TurnStarted = new TurnStarted { Model = "model" } }, cancellationToken);
        _ = await Assert.That(fixture.LastDrawn).Contains("visible ancestor");
        _ = await Assert.That(fixture.LastDrawn).Contains("hidden descendant");
        await fixture.View.Render(new Event { AgentSessionId = "worker", TurnEnded = new TurnEnded { FinishReason = "stop" } }, cancellationToken);
        _ = await Assert.That(fixture.LastDrawn).DoesNotContain("hidden descendant");
    }

    [Test]
    public async Task Terminal_tree_kept_for_active_worker_retires_after_activity_stops(CancellationToken cancellationToken)
    {
        var progressFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new RawActivityRecorder(
            120,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            (_, token) => progressFlush.Task.WaitAsync(token));
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
        progressFlush.SetResult();
        await fixture.CommitObserved.Task.WaitAsync(cancellationToken);
        await fixture.View.Render(new Event { AgentSessionId = "worker", TurnEnded = new TurnEnded { FinishReason = "stop" } }, cancellationToken);
        _ = await Assert.That(fixture.LastDrawn).DoesNotContain("Agent tasks:");
        _ = await Assert.That(fixture.LastDrawn).DoesNotContain("finished task");
        _ = await Assert.That(fixture.CommittedText).Contains("finished task");
    }
}
