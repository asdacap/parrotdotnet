using Parrot.Agent;
using Parrot.Context;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Questions;
using Parrot.Security;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class AgentInterruptToolTests
{
    [Test]
    [Arguments("", false)]
    [Arguments("   ", false)]
    [Arguments("missing", false)]
    [Arguments("worker", true)]
    public async Task Interrupt_requests_resolve_through_the_direct_child_registry(
        string name,
        bool expectedInterrupt,
        CancellationToken cancellationToken)
    {
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = MakeInterruptingChild(interrupted);
        var registry = new StaticChildRegistry([("worker", child)]);
        ITool tool = new AgentInterruptTool(registry);

        var result = await tool.Execute(
            new ToolInvocation("call-1", $"{{\"name\":\"{name}\"}}"),
            new AgentTurnSelection(
                new ModelSelector("test/model"),
                TestModels.Resolve(new ProviderModel(new UnusedProvider(), new LLMModel("model", "test"))),
                new TestProfileFixture().Mode,
                SecurityProfile.Compose(readOnly: false, [], [], [])),
            cancellationToken);

        if (!expectedInterrupt)
        {
            _ = await Assert.That(result.Text).StartsWith("error: ");
        }
        else
        {
            _ = await Assert.That(result.Text).Contains("Interrupt requested");
            _ = await Assert.That(result.Text).Contains("worker");
            await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            _ = await Assert.That(child.InterruptCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Extra_arguments_are_rejected(CancellationToken cancellationToken)
    {
        var child = MakeChild();
        var registry = new StaticChildRegistry([("worker", child)]);
        ITool tool = new AgentInterruptTool(registry);

        var result = await tool.Execute(
            new ToolInvocation("call", "{\"name\":\"worker\",\"extra\":1}"),
            new AgentTurnSelection(
                new ModelSelector("test/model"),
                TestModels.Resolve(new ProviderModel(new UnusedProvider(), new LLMModel("model", "test"))),
                new TestProfileFixture().Mode,
                SecurityProfile.Compose(readOnly: false, [], [], [])),
            cancellationToken);

        _ = await Assert.That(result.Text).StartsWith("error: ");
        _ = await Assert.That(child.InterruptCount).IsEqualTo(0);
    }

    private static RecordingChildSession MakeChild() => new(() => { });

    private static RecordingChildSession MakeInterruptingChild(TaskCompletionSource interrupted) => new(() => _ = interrupted.TrySetResult());

    private sealed class StaticChildRegistry(IReadOnlyList<(string Name, RecordingChildSession Session)> children) : IChildRegistry
    {
        public bool IsAccepting => true;

        public Lock Gate => throw new NotSupportedException();

        public ValueTask DisposeChildren() => ValueTask.CompletedTask;

        public IReadOnlyList<IAgentSessionScope> SnapshotChildScopes() =>
            [.. children.Select(child => new StaticChildScope(child.Session))];

        public IAgentSessionScope? DetachDirectChildScope(IAgentSessionScope scope) => null;

        public bool ContainsDescendantScope(IAgentSessionScope candidate) => false;

        public IReadOnlyList<IAgentSession> SnapshotDescendants() => [];

        public IAgentSessionScope? FindNamedChildScope(string name) =>
            children.Where(child => child.Name == name)
                .Select(child => (IAgentSessionScope)new StaticChildScope(child.Session))
                .FirstOrDefault();

        public IAgentSessionScope ResolveNamedChildScope(string name) =>
            children.Where(child => child.Name == name)
                .Select(child => (IAgentSessionScope)new StaticChildScope(child.Session))
                .FirstOrDefault()
                ?? throw new AgentRegistryException($"direct child not found: {name}");

        public bool TryAdd(IAgentSessionScope scope) => throw new NotSupportedException();
    }

    private sealed class StaticChildScope(RecordingChildSession session) : IAgentSessionScope
    {
        public IAgentSession Session => session;

        public IGoalService Goals => throw new NotSupportedException();

        public IAgentSpawner AgentSpawner => throw new NotSupportedException();

        public IChildRegistry ChildRegistry => throw new NotSupportedException();

        public IAgentParentScope ParentScope => throw new NotSupportedException();

        public IChildQuestionCoordinator ChildQuestions => throw new NotSupportedException();

        public T GetService<T>()
        where T : class => throw new NotSupportedException();

        public void PublishSnapshots() => throw new NotSupportedException();

        public IReadOnlyList<Parrot.Protocol.Event> CaptureSnapshotEvents() => throw new NotSupportedException();

        public Task SettleWork() => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingChildSession(Action onInterrupt) : IAgentSession
    {
        public int InterruptCount { get; private set; }

        public string SessionId => "child-session";

        public string Name => "worker";

        public string ParentSessionId => string.Empty;

        public string ParentSessionName => string.Empty;

        public int Depth => 1;

        public AgentIdentity Identity { get; } = AgentIdentity.Main("child-session", "worker", TestModels.PromptTemplates);

        public string OutputPath => throw new NotSupportedException();

        public AgentSessionActivity Activity { get; } = new(TimeProvider.System);

        public TurnInterruptionRequest TurnInterruption { get; } = new();

        public AgentSessionStatisticsSnapshot CaptureStatistics() => throw new NotSupportedException();

        public void AddDescendantUsage(AgentUsageIncrement increment) => throw new NotSupportedException();

        public AgentSelection CurrentSelection() => throw new NotSupportedException();

        public void UseResolvedSelection(ResolvedModelSelection selectedModel) => throw new NotSupportedException();

        public void Recover() => throw new NotSupportedException();

        public void UpdateSelection(ModelSelector selectedModel, IMode mode) => throw new NotSupportedException();

        public bool Wake(IncomingActivity? activity) => throw new NotSupportedException();

        public Task Compact(ContextSize? targetContextSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task Settled() => throw new NotSupportedException();

        public AgentSelection ResolvePolicySelection() => throw new NotSupportedException();

        public AgentPolicyLineage ResolvePolicyLineage() => throw new NotSupportedException();

        public bool IsIdle() => throw new NotSupportedException();

        public bool IsActive() => throw new NotSupportedException();

        public Task<IncomingActivity?> WaitForIncomingInput(
            TimeSpan duration,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<(Admission Admission, bool FollowUp)> Send(
            IReadOnlyList<ConversationPart> parts,
            string messageId,
            Delivery delivery,
            IncomingActivity reason,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetExitReminder(string? reminder, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AgentSendResult> SendTextMessage(string message, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> SendAndWaitForResult(string prompt, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task Record(IReadOnlyList<ConversationPart> parts, string messageId, Delivery delivery, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WaitAgentResult> Wait(int yieldAfterMilliseconds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ContextSnapshot EstimateContext(AgentTurnSelection selection) => throw new NotSupportedException();

        public IReadOnlyList<LLMToolDefinition> AdvertisedToolDefinitions(AgentTurnSelection selection) => throw new NotSupportedException();

        public ContextSnapshot EstimateContextForTools(AgentTurnSelection selection, IReadOnlyList<LLMToolDefinition> tools) => throw new NotSupportedException();

        public ContextSnapshot EstimateContextAfterToolResult(AgentTurnSelection selection, string toolCallId, string result) => throw new NotSupportedException();

        public Task<ContextCompactionResult> CompactFromTool(AgentTurnSelection selection, ContextSize? targetContextSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task Interrupt(CancellationToken cancellationToken)
        {
            InterruptCount++;
            onInterrupt();
            return Task.CompletedTask;
        }
    }
}
