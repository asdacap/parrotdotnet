using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class AgentTaskProgressStateTests
{
    [Test]
    public async Task Agent_keeps_its_newest_graph_until_a_settled_revision_retires_and_a_later_update_revives_it()
    {
        var state = new AgentSessionState("main");

        _ = await Assert.That(state.OfferAgentTaskProgress(Snapshot(1, AgentTaskProgressStatus.Running))).IsTrue();
        _ = await Assert.That(state.OfferAgentTaskProgress(Snapshot(1, AgentTaskProgressStatus.Succeeded))).IsFalse();
        _ = await Assert.That(state.RetireAgentTaskProgress(1)).IsFalse();
        _ = await Assert.That(state.OfferAgentTaskProgress(Snapshot(2, AgentTaskProgressStatus.Failed))).IsTrue();
        _ = await Assert.That(state.RetireAgentTaskProgress(2)).IsFalse();
        _ = state.FinishTool(
            new Event { ToolFinished = new ToolFinished { ToolCallId = "call", ToolName = "set_agent_tasks" } },
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static value => value,
            false);
        _ = await Assert.That(state.AgentTaskProgress?.Revision).IsEqualTo(2UL);
        _ = await Assert.That(state.OfferAgentTaskProgress(Snapshot(3, AgentTaskProgressStatus.Canceled))).IsTrue();
        _ = await Assert.That(state.IsCurrentAgentTaskProgress(2)).IsFalse();
        _ = await Assert.That(state.RetireAgentTaskProgress(2)).IsFalse();
        _ = await Assert.That(state.RetireAgentTaskProgress(3)).IsTrue();
        _ = await Assert.That(state.AgentTaskProgress).IsNull();
        _ = await Assert.That(state.OfferAgentTaskProgress(Snapshot(4, AgentTaskProgressStatus.Running))).IsTrue();
        _ = await Assert.That(state.AgentTaskProgress?.RootNodes[1].Status).IsEqualTo(AgentTaskProgressStatus.Running);
    }

    private static AgentTaskProgressSnapshot Snapshot(ulong revision, AgentTaskProgressStatus status) => new()
    {
        Revision = revision,
        RootNodes =
        {
            new AgentTaskProgressNode { Name = "done", Status = AgentTaskProgressStatus.Succeeded },
            new AgentTaskProgressNode { Name = "task", Status = status },
        },
    };
}
