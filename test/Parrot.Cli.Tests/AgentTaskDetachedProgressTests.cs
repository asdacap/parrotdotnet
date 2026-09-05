using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class AgentTaskDetachedProgressTests
{
    [Test]
    public async Task Progress_survives_tool_finished_and_accepts_later_terminal_snapshot()
    {
        var state = new AgentSessionState("main");
        state.CollectToolCall(new ToolCallChunk { ToolCallId = "call", ToolName = "run_agent_tasks" });
        _ = state.StartTool(new ToolStarted { ToolCallId = "call", ToolName = "run_agent_tasks" }, false);
        _ = state.OfferAgentTaskProgress(Snapshot("call", 1, AgentTaskProgressStatus.Running));
        _ = state.FinishTool(
            new Event { ToolFinished = new ToolFinished { ToolCallId = "call", ToolName = "run_agent_tasks" } },
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static value => value);

        _ = await Assert.That(state.OfferAgentTaskProgress(Snapshot("call", 2, AgentTaskProgressStatus.Succeeded))).IsTrue();
        _ = await Assert.That(state.IsDetachedAgentTask("call")).IsTrue();
        _ = await Assert.That(state.RetireDetachedAgentTaskProgress("call", 2)).IsTrue();
        _ = await Assert.That(state.IsDetachedAgentTask("call")).IsFalse();
    }

    [Test]
    public async Task Terminal_snapshot_before_tool_finished_is_retained_once_and_rejected_afterwards()
    {
        var state = new AgentSessionState("main");
        state.CollectToolCall(new ToolCallChunk { ToolCallId = "call", ToolName = "run_agent_tasks" });
        _ = state.StartTool(new ToolStarted { ToolCallId = "call", ToolName = "run_agent_tasks" }, false);
        _ = state.OfferAgentTaskProgress(Snapshot("call", 1, AgentTaskProgressStatus.Succeeded));
        _ = state.FinishTool(
            new Event { ToolFinished = new ToolFinished { ToolCallId = "call", ToolName = "run_agent_tasks" } },
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static value => value);

        _ = await Assert.That(state.IsDetachedAgentTask("call")).IsFalse();
        _ = await Assert.That(state.OfferAgentTaskProgress(Snapshot("call", 2, AgentTaskProgressStatus.Succeeded))).IsFalse();
    }

    [Test]
    public async Task Tool_finished_without_observed_progress_does_not_detach_or_throw()
    {
        var state = new AgentSessionState("main");

        _ = state.FinishTool(
            new Event { ToolFinished = new ToolFinished { ToolCallId = "missing", ToolName = "run_agent_tasks" } },
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static value => value);

        _ = await Assert.That(state.IsDetachedAgentTask("missing")).IsFalse();
        _ = await Assert.That(state.DetachedAgentTaskProgressIds()).IsEmpty();
    }

    [Test]
    public async Task Multiple_detached_graphs_keep_independent_revisions()
    {
        var state = new AgentSessionState("main");
        foreach (var callId in new[] { "first", "second" })
        {
            state.CollectToolCall(new ToolCallChunk { ToolCallId = callId, ToolName = "run_agent_tasks" });
            _ = state.StartTool(new ToolStarted { ToolCallId = callId, ToolName = "run_agent_tasks" }, false);
            _ = state.OfferAgentTaskProgress(Snapshot(callId, 1, AgentTaskProgressStatus.Running));
            _ = state.FinishTool(
                new Event { ToolFinished = new ToolFinished { ToolCallId = callId, ToolName = "run_agent_tasks" } },
                new ToolPresenterRegistry([], new GenericToolPresenter()),
                static value => value);
        }

        _ = await Assert.That(state.OfferAgentTaskProgress(Snapshot("first", 1, AgentTaskProgressStatus.Succeeded))).IsFalse();
        _ = await Assert.That(state.OfferAgentTaskProgress(Snapshot("second", 2, AgentTaskProgressStatus.Succeeded))).IsTrue();
    }

    private static AgentTaskProgressSnapshot Snapshot(string callId, ulong revision, AgentTaskProgressStatus status)
    {
        var snapshot = new AgentTaskProgressSnapshot { OriginToolCallId = callId, Revision = revision };
        snapshot.RootNodes.Add(new AgentTaskProgressNode { Name = callId, Status = status });
        return snapshot;
    }
}
