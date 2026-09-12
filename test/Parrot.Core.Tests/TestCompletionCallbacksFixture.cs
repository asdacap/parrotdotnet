using Parrot.Agent;
using Parrot.Events;
using Parrot.Questions;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class TestCompletionCallbacksFixture(
    IChildQuestionCoordinator childQuestions,
    ActiveWorkCompletionReminder activeWorkReminder,
    ExitReminder exitReminder,
    IEventRepository eventRepository,
    IEventBroker eventBroker)
{
    public IReadOnlyList<IAgentTurnCompletionCallback> Callbacks { get; } =
    [
        new PendingChildQuestionTurnCompletionCallback(childQuestions, eventRepository, eventBroker),
        new ActiveWorkTurnCompletionCallback(activeWorkReminder, eventRepository, eventBroker),
        new ModeTurnCompletionCallback(eventRepository, eventBroker),
        new ExitReminderTurnCompletionCallback(exitReminder, eventRepository, eventBroker),
    ];
}
