using Parrot.Agent;
using Parrot.Questions;
using Parrot.Queues;
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
        IEventRepository eventRepository,
        IRuntimeStatus status,
        IAgentRegistry registry,
        IAgentQueues queues,
        IChildRegistry children)
    {
        _children = children;
        _processOwner = processOwner;
        ChildQuestions = new ChildQuestionCoordinator(AgentSessionParentScope.Root(), TestModels.PromptTemplates);
        ActiveWorkReminder = new ActiveWorkCompletionReminder([new ChildAgentActiveWorkBlocker(_children, identity), new ProcessActiveWorkBlocker(processOwner), new QueueActiveWorkBlocker(queues, TestModels.PromptTemplates)], TestModels.PromptTemplates);
        ExitReminder = new ExitReminder(eventRepository, TestModels.PromptTemplates, identity.SessionId);
        Profile = new TestProfileFixture().Mode;
        Status = status;
        Registry = registry;
        Queues = queues;
    }

    public IChildQuestionCoordinator ChildQuestions { get; }

    public ActiveWorkCompletionReminder ActiveWorkReminder { get; }

    public IExitReminder ExitReminder { get; }

    public IMode Profile { get; }

    public IRuntimeStatus Status { get; }

    public IAgentRegistry Registry { get; }

    public IAgentQueues Queues { get; }

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
