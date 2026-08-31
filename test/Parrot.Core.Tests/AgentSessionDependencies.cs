using Parrot.Agent;
using Parrot.Queues;
using Parrot.Statuses;

namespace Parrot.Core.Tests;

internal sealed record AgentSessionDependencies(
    ActiveWorkCompletionReminder ActiveWorkReminder,
    IMode Profile,
    RuntimeStatus Status,
    AgentRegistry Registry,
    AgentQueues Queues);
