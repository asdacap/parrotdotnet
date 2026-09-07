using Parrot.Agent;
using Parrot.Events;
using Parrot.Questions;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class TestCompletionCallbacksFixture(
    ChildQuestionCoordinator childQuestions,
    ActiveWorkCompletionReminder activeWorkReminder,
    ExitReminder exitReminder,
    EventRepository eventRepository,
    EventBroker eventBroker)
{
    public IReadOnlyList<IAgentTurnCompletionCallback> Callbacks { get; } =
    [
        new PendingChildQuestionTurnCompletionCallback(childQuestions, eventRepository, eventBroker),
        new ActiveWorkTurnCompletionCallback(activeWorkReminder, eventRepository, eventBroker),
        new ModeTurnCompletionCallback(eventRepository, eventBroker),
        new ExitReminderTurnCompletionCallback(exitReminder, eventRepository, eventBroker),
    ];
}
