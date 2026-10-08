using Parrot.Agent;
using Parrot.Context;

namespace Parrot.Statuses;

/// <summary>Observes runtime, statistics, and context status for an agent session.</summary>
internal interface IRuntimeStatus
{
    Task<string> ObserveRuntime(
        IAgentSession session,
        AgentTurnSelection selection,
        CancellationToken cancellationToken);

    Task<string> ObserveStatistics(
        IAgentSession session,
        AgentTurnSelection selection,
        CancellationToken cancellationToken);

    Task<string> ObserveWithContext(
        IAgentSession session,
        AgentTurnSelection selection,
        IAgentProfile profile,
        ContextSnapshot contextSnapshot,
        CancellationToken cancellationToken);

    Task<string> ObserveContext(
        IAgentSession session,
        AgentTurnSelection selection,
        ContextSnapshot contextSnapshot,
        CancellationToken cancellationToken);
}
