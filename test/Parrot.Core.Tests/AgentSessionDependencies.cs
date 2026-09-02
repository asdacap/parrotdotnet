using Parrot.Agent;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Statuses;

namespace Parrot.Core.Tests;

internal sealed record AgentSessionDependencies(
    ChildQuestionCoordinator ChildQuestions,
    ActiveWorkCompletionReminder ActiveWorkReminder,
    IMode Profile,
    RuntimeStatus Status,
    AgentRegistry Registry,
    AgentQueues Queues);
