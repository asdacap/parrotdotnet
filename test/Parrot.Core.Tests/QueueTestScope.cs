using Parrot.Agent;
using Parrot.AgentTasks;
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
    private readonly IEventBroker _events = new EventBroker();
    private readonly AgentSessionDependencies _dependencies;
    private readonly ChildRegistry _children;
    private readonly QueueTestScope? _parent;
    private readonly AgentSessionServices _services = new();
    private readonly IAgentQueues _queues;
    private bool _disposed;

    public QueueTestScope(AgentIdentity identity, QueueTestScope? parent, UserSessionResources resources)
    {
        _parent = parent;
        _children = new ChildRegistry(identity, QueueChildAdmissionValidator.Validate);
        AgentTaskRuns = new AgentTaskRunCatalog(identity.SessionId, TestDiagnosticLog.Instance, CancellationToken.None);
        var repository = new EventRepository(_database);
        _dependencies = TestModels.Dependencies(identity, _events, repository, CancellationToken.None);
        Processes = new ShellProcessOwner(identity, resources, new AgentPathEnvironment(resources, resources.AgentScratch(identity.SessionId)), new ProcessRunner(string.Empty), TestDiagnosticLog.Instance, CancellationToken.None);
        _queues = new AgentQueues(identity, parent?.GetService<IAgentQueues>(), resources, _children, static queueIdentity => new QueueInventory(queueIdentity), TestDiagnosticLog.Instance);
        _services.Register<IAgentQueues>(_queues);
        _queues.Initialize();
        ParentScope = parent is null ? AgentSessionParentScope.Root() : AgentSessionParentScope.Child(parent, AgentCompletionDeliveryPolicy.RetainedOnly);
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        Session = new AgentSession(identity, ParentScope, new ModelSelector(model.Selector), TestModels.Route(model), _events, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(resources.AgentScratch(identity.SessionId).Root), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(TestDiagnosticLog.Instance, identity.SessionId, null), new ContextCadence(), TestModels.PromptTemplates, ChildQuestions, _dependencies.ExitReminder, _dependencies.Profile, [], new SecurityProfileTestFixture(SecurityProfile.Compose(false, [], [], [])).Security, _dependencies.Status, _queues, new AgentSessionActivity(TimeProvider.System), TestDiagnosticLog.Instance, CancellationToken.None);
        if (parent is not null && !parent.ChildRegistry.TryAdd(this))
        {
            throw new InvalidOperationException("The parent is no longer accepting children.");
        }
    }

    public IAgentSession Session { get; }

    public IProcessOwner Processes { get; }

    public IAgentTaskRunCatalog AgentTaskRuns { get; }

    public IGoalService Goals => throw new NotSupportedException();

    public IAgentSpawner AgentSpawner => throw new NotSupportedException();

    public IChildRegistry ChildRegistry => _children;

    public IAgentParentScope ParentScope { get; }

    public IChildQuestionCoordinator ChildQuestions => _dependencies.ChildQuestions;

    public T GetService<T>()
        where T : class => _services.GetService<T>();

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

        await AgentTaskRuns.DisposeAsync().ConfigureAwait(false);
        await _children.DisposeAsync().ConfigureAwait(false);
        await Session.DisposeAsync().ConfigureAwait(false);
        _queues.Dispose();
        await Processes.DisposeAsync().ConfigureAwait(false);
        await _dependencies.DisposeAsync().ConfigureAwait(false);
        _events.Dispose();
        _database.Dispose();
    }
}
