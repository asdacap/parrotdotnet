using Parrot.Agent;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentSessionDependencies : IDisposable, IAsyncDisposable
{
    private readonly ChildRegistry _children;

    internal AgentSessionDependencies(
        AgentIdentity identity,
        Process.ShellProcessOwner processOwner,
        EventRepository eventRepository,
        RuntimeStatus status,
        AgentRegistry registry,
        AgentQueues queues)
    {
        _children = new ChildRegistry(identity);
        ChildQuestions = new ChildQuestionCoordinator(AgentSessionParentScope.Root(), TestModels.PromptTemplates);
        ActiveWorkReminder = new ActiveWorkCompletionReminder(_children, processOwner, TestModels.PromptTemplates);
        ExitReminder = new ExitReminder(eventRepository, TestModels.PromptTemplates, identity.SessionId);
        Profile = TestModels.Profile();
        Status = status;
        Registry = registry;
        Queues = queues;
    }

    public ChildQuestionCoordinator ChildQuestions { get; }

    public ActiveWorkCompletionReminder ActiveWorkReminder { get; }

    public ExitReminder ExitReminder { get; }

    public IMode Profile { get; }

    public RuntimeStatus Status { get; }

    public AgentRegistry Registry { get; }

    public AgentQueues Queues { get; }

    public void Dispose() => ChildQuestions.Dispose();

    public async ValueTask DisposeAsync()
    {
        await _children.DisposeAsync().ConfigureAwait(false);
        ChildQuestions.Dispose();
    }
}
