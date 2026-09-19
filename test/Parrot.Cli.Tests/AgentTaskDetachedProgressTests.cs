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
        _ = state.OfferAgentTaskProgress(new ProgressFixture("call", 1, AgentTaskProgressStatus.Running).Snapshot);
        _ = state.FinishTool(
            new Event { ToolFinished = new ToolFinished { ToolCallId = "call", ToolName = "run_agent_tasks" } },
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static value => value);

        _ = await Assert.That(state.OfferAgentTaskProgress(new ProgressFixture("call", 2, AgentTaskProgressStatus.Succeeded).Snapshot)).IsTrue();
        _ = await Assert.That(state.RetireDetachedAgentTaskProgress("call", 2)).IsTrue();
    }

    [Test]
    public async Task Terminal_snapshot_before_tool_finished_is_retained_once_and_rejected_afterwards()
    {
        var state = new AgentSessionState("main");
        state.CollectToolCall(new ToolCallChunk { ToolCallId = "call", ToolName = "run_agent_tasks" });
        _ = state.StartTool(new ToolStarted { ToolCallId = "call", ToolName = "run_agent_tasks" }, false);
        _ = state.OfferAgentTaskProgress(new ProgressFixture("call", 1, AgentTaskProgressStatus.Succeeded).Snapshot);
        _ = state.FinishTool(
            new Event { ToolFinished = new ToolFinished { ToolCallId = "call", ToolName = "run_agent_tasks" } },
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static value => value);

        _ = await Assert.That(state.OfferAgentTaskProgress(new ProgressFixture("call", 2, AgentTaskProgressStatus.Succeeded).Snapshot)).IsFalse();
    }

    [Test]
    public async Task Tool_finished_without_observed_progress_does_not_detach_or_throw()
    {
        var state = new AgentSessionState("main");

        _ = state.FinishTool(
            new Event { ToolFinished = new ToolFinished { ToolCallId = "missing", ToolName = "run_agent_tasks" } },
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static value => value);

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
            _ = state.OfferAgentTaskProgress(new ProgressFixture(callId, 1, AgentTaskProgressStatus.Running).Snapshot);
            _ = state.FinishTool(
                new Event { ToolFinished = new ToolFinished { ToolCallId = callId, ToolName = "run_agent_tasks" } },
                new ToolPresenterRegistry([], new GenericToolPresenter()),
                static value => value);
        }

        _ = await Assert.That(state.OfferAgentTaskProgress(new ProgressFixture("first", 1, AgentTaskProgressStatus.Succeeded).Snapshot)).IsFalse();
        _ = await Assert.That(state.OfferAgentTaskProgress(new ProgressFixture("second", 2, AgentTaskProgressStatus.Succeeded).Snapshot)).IsTrue();
    }

    private sealed class ProgressFixture(string callId, ulong revision, AgentTaskProgressStatus status)
    {
        public AgentTaskProgressSnapshot Snapshot { get; } = new()
        {
            OriginToolCallId = callId,
            Revision = revision,
            RootNodes = { new AgentTaskProgressNode { Name = callId, Status = status } },
        };
    }
}
