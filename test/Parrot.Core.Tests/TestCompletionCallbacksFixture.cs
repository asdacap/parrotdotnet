using Parrot.Agent;
using Parrot.Events;
using Parrot.Questions;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class TestCompletionCallbacksFixture(
    IChildQuestionCoordinator childQuestions,
    ActiveWorkCompletionReminder activeWorkReminder,
    IExitReminder exitReminder,
    IAgentHistory history,
    IEventBroker eventBroker)
{
    public IReadOnlyList<IAgentTurnCompletionCallback> Callbacks { get; } =
    [
        new PendingChildQuestionTurnCompletionCallback(childQuestions, history, eventBroker),
        new ActiveWorkTurnCompletionCallback(activeWorkReminder, history, eventBroker),
        new ExitReminderTurnCompletionCallback(exitReminder, history, eventBroker),
    ];
}
