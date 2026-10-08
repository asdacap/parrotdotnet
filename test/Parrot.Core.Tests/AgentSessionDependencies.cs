using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Security;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentSessionDependencies : IDisposable, IAsyncDisposable
{
    private readonly IChildRegistry _children;
    private readonly Process.IProcessOwner _processOwner;

    internal AgentSessionDependencies(
        AgentIdentity identity,
        Process.IProcessOwner processOwner,
        IEventBroker eventBroker,
        IEventRepository eventRepository,
        IRuntimeStatus status,
        IAgentRegistry registry,
        IAgentQueues queues,
        IChildRegistry children)
    {
        _children = children;
        _processOwner = processOwner;
        ChildQuestions = new ChildQuestionCoordinator(AgentSessionParentScope.Root(), children, TestModels.PromptTemplates);
        ActiveWorkReminder = new ActiveWorkCompletionReminder([new ChildAgentActiveWorkBlocker(_children, identity), new ProcessActiveWorkBlocker(processOwner), new QueueActiveWorkBlocker(queues, TestModels.PromptTemplates)], TestModels.PromptTemplates);
        ExitReminder = new ExitReminder(eventRepository, eventBroker, TestModels.PromptTemplates, identity.SessionId);
        Profile = new TestProfileFixture().Profile;
        Status = status;
        Registry = registry;
        Queues = queues;
    }

    public IChildQuestionCoordinator ChildQuestions { get; }

    public ActiveWorkCompletionReminder ActiveWorkReminder { get; }

    public IExitReminder ExitReminder { get; }

    public IAgentProfile Profile { get; }

    public IRuntimeStatus Status { get; }

    public IAgentRegistry Registry { get; }

    public IAgentQueues Queues { get; }

    public AgentSession CreateRootSession(
        AgentIdentity identity,
        ProviderModel model,
        IEventBroker eventBroker,
        IEventRepository eventRepository,
        string promptDirectory,
        string blobDirectory,
        CompactionGroupBlobStore compactionGroupBlobs,
        Compactor compactor,
        CancellationToken lifetime) =>
        new(
            identity,
            AgentSessionParentScope.Root(),
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            eventBroker,
            eventRepository,
            [],
            TestModels.MaterializePrompt(identity, promptDirectory, promptDirectory),
            new ToolOutputBlobStore(blobDirectory),
            new AgentOutputFile(blobDirectory),
            compactionGroupBlobs,
            compactor,
            new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null),
            new ContextCadence(),
            TestModels.PromptTemplates,
            ChildQuestions,
            ExitReminder,
            Profile,
            new TestCompletionCallbacksFixture(ChildQuestions, ActiveWorkReminder, ExitReminder, eventRepository, eventBroker).Callbacks,
            new SecurityProfileTestFixture(SecurityProfile.Compose(readOnly: false, [], [], [])).Security,
            Status,
            new AgentSessionActivity(TimeProvider.System),
            TestDiagnosticLog.Instance,
            lifetime);

    public void Dispose() => ChildQuestions.Dispose();

    public async ValueTask DisposeAsync()
    {
        await _children.DisposeChildren().ConfigureAwait(false);
        await _processOwner.DisposeAsync().ConfigureAwait(false);
        Queues.Dispose();
        await Registry.DisposeAsync().ConfigureAwait(false);
        ChildQuestions.Dispose();
    }
}
