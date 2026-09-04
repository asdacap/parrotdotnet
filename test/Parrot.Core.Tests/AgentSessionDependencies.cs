using Parrot.Agent;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Statuses;

namespace Parrot.Core.Tests;

internal sealed record AgentSessionDependencies(
    ChildQuestionCoordinator ChildQuestions,
    ActiveWorkCompletionReminder ActiveWorkReminder,
    ExitReminder ExitReminder,
    IMode Profile,
    RuntimeStatus Status,
    AgentRegistry Registry,
    AgentQueues Queues) : IDisposable
{
    public void Dispose() => ChildQuestions.Dispose();
}
