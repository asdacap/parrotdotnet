using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class QueueTestScope : IAgentSessionScope
{
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly EventBroker _events = new();
    private readonly AgentSessionDependencies _dependencies;
    private readonly ChildRegistry _children;
    private readonly QueueTestScope? _parent;
    private bool _disposed;

    public QueueTestScope(AgentIdentity identity, QueueTestScope? parent, UserSessionResources resources)
    {
        _parent = parent;
        _children = new ChildRegistry(identity);
        var repository = new EventRepository(_database);
        _dependencies = TestModels.Dependencies(identity, _events, repository, CancellationToken.None);
        Processes = new ShellProcessOwner(identity, resources, new AgentPathEnvironment(resources, resources.AgentScratch(identity.SessionId)), new ProcessRunner(string.Empty), TestDiagnosticLog.Instance, CancellationToken.None);
        Queues = new AgentQueues(identity, parent?.Queues, resources, _children, TestDiagnosticLog.Instance);
        Queues.Initialize();
        ParentScope = parent is null ? AgentSessionParentScope.Root() : AgentSessionParentScope.Child(parent, AgentCompletionDeliveryPolicy.RetainedOnly);
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        Session = new AgentSession(identity, ParentScope, new ModelSelector(model.Selector), TestModels.Route(model), _events, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(resources.AgentScratch(identity.SessionId).Root), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(TestDiagnosticLog.Instance, identity.SessionId), new ContextCadence(), TestModels.PromptTemplates, ChildQuestions, _dependencies.ExitReminder, _dependencies.Profile, [], new SecurityProfileTestFixture(SecurityProfile.Compose(false, [], [], [])).Security, _dependencies.Status, Queues, new AgentSessionActivity(TimeProvider.System), TestDiagnosticLog.Instance, CancellationToken.None);
        if (parent is not null && !parent.ChildRegistry.TryAdd(this))
        {
            throw new InvalidOperationException("The parent is no longer accepting children.");
        }
    }

    public IAgentSession Session { get; }

    public ShellProcessOwner Processes { get; }

    public AgentQueues Queues { get; }

    public GoalService Goals => throw new NotSupportedException();

    public AgentSpawner AgentSpawner => throw new NotSupportedException();

    public IChildRegistry ChildRegistry => _children;

    public AgentSessionParentScope ParentScope { get; }

    public ChildQuestionCoordinator ChildQuestions => _dependencies.ChildQuestions;

    public void PublishInventories()
    {
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_parent is not null)
        {
            _ = _parent.ChildRegistry.DetachDirectChildScope(this);
        }

        await _children.DisposeAsync().ConfigureAwait(false);
        await Session.DisposeAsync().ConfigureAwait(false);
        Queues.Dispose();
        await Processes.DisposeAsync().ConfigureAwait(false);
        await _dependencies.DisposeAsync().ConfigureAwait(false);
        _events.Dispose();
        _database.Dispose();
    }
}
