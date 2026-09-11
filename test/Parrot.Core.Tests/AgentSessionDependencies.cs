using Parrot.Agent;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentSessionDependencies : IDisposable, IAsyncDisposable
{
    private readonly IChildRegistry _children;
    private readonly Process.ShellProcessOwner _processOwner;

    internal AgentSessionDependencies(
        AgentIdentity identity,
        Process.ShellProcessOwner processOwner,
        EventRepository eventRepository,
        RuntimeStatus status,
        IAgentRegistry registry,
        AgentQueues queues,
        IChildRegistry children)
    {
        _children = children;
        _processOwner = processOwner;
        ChildQuestions = new ChildQuestionCoordinator(AgentSessionParentScope.Root(), TestModels.PromptTemplates);
        ActiveWorkReminder = new ActiveWorkCompletionReminder(_children, processOwner, TestModels.PromptTemplates, null);
        ExitReminder = new ExitReminder(eventRepository, TestModels.PromptTemplates, identity.SessionId);
        Profile = new TestProfileFixture().Mode;
        Status = status;
        Registry = registry;
        Queues = queues;
    }

    public ChildQuestionCoordinator ChildQuestions { get; }

    public ActiveWorkCompletionReminder ActiveWorkReminder { get; }

    public ExitReminder ExitReminder { get; }

    public IMode Profile { get; }

    public RuntimeStatus Status { get; }

    public IAgentRegistry Registry { get; }

    public AgentQueues Queues { get; }

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
