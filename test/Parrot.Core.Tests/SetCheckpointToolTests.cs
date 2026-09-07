using Parrot.Agent;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class SetCheckpointToolTests
{
    [Test]
    public async Task Records_durable_checkpoint_for_owning_agent_and_isolates_other_agents()
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        const string callId = "checkpoint-call";
        repository.AppendConversation(
            new Event { Id = "assistant", AgentSessionId = "agent-a" },
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart(string.Empty)],
            [new LLMToolCall(callId, "set_checkpoint", "{\"title\":\"handoff\"}")],
            string.Empty);
        var assistantSequence = repository.Conversation("agent-a").Single().Sequence;
        ITool owningTool = new SetCheckpointTool(new CheckpointService(repository, "agent-a"));
        ITool otherAgentTool = new SetCheckpointTool(new CheckpointService(repository, "agent-b"));
        var invocation = new ToolInvocation(callId, "{\"title\":\"handoff\"}", assistantSequence);

        var result = await owningTool.Execute(invocation, new SelectionFixture().Selection, CancellationToken.None);
        var isolatedResult = await otherAgentTool.Execute(invocation, new SelectionFixture().Selection, CancellationToken.None);

        _ = await Assert.That(result.Text).IsEqualTo("handoff");
        var checkpoint = repository.LatestUsableCheckpoint("agent-a", "handoff", long.MaxValue)
            ?? throw new InvalidOperationException("Expected a durable checkpoint.");
        _ = await Assert.That(checkpoint.AssistantSequence).IsEqualTo(assistantSequence);
        _ = await Assert.That(checkpoint.ToolCallId).IsEqualTo(callId);
        _ = await Assert.That(repository.LatestUsableCheckpoint("agent-b", "handoff", long.MaxValue)).IsNull();
        _ = await Assert.That(isolatedResult.Text).StartsWith("error:");
        _ = await Assert.That(isolatedResult.Text).Contains("already recorded differently");
    }

    [Test]
    [Arguments("{", 1, "error:")]
    [Arguments("null", 1, "Tool arguments must be an object.")]
    [Arguments("{}", 1, "Tool arguments require a string 'title'.")]
    [Arguments("{\"title\":null}", 1, "Tool arguments require a string 'title'.")]
    [Arguments("{\"title\":\"  \"}", 1, "checkpoint title must not be blank")]
    [Arguments("{\"title\":\"handoff\"}", 0, "checkpoint requires a durable assistant tool batch")]
    [Arguments("{\"title\":\"handoff\"}", 42, "already recorded differently")]
    public async Task Reports_invalid_or_unrecordable_invocations(
        string argumentsJson,
        long assistantSequence,
        string expectedMessage)
    {
        using var database = SessionDatabase.Open(":memory:");
        ITool tool = new SetCheckpointTool(new CheckpointService(new EventRepository(database), "agent"));

        var result = await tool.Execute(
            new ToolInvocation("missing-call", argumentsJson, assistantSequence),
            new SelectionFixture().Selection,
            CancellationToken.None);

        _ = await Assert.That(result.Text).StartsWith("error:");
        _ = await Assert.That(result.Text).Contains(expectedMessage);
    }

    private sealed class SelectionFixture
    {
        public SelectionFixture()
        {
            ILLMProvider provider = new UnusedProvider();
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
            Selection = new AgentTurnSelection(
                new ModelSelector(model.Selector),
                TestModels.Resolve(model),
                new TestProfileFixture().Mode,
                SecurityProfile.Compose(readOnly: false, [], [], []));
        }

        public AgentTurnSelection Selection { get; }
    }
}
