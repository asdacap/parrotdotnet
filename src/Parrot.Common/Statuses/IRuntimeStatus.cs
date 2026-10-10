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

    /// <summary>
    /// Observes the full status whose context section accounts for the status itself, re-observing with
    /// <paramref name="estimate"/> of the candidate content until the context converges.
    /// </summary>
    Task<string> ObserveFitted(
        IAgentSession session,
        AgentTurnSelection selection,
        IAgentProfile profile,
        Func<string, ContextSnapshot> estimate,
        CancellationToken cancellationToken);
}
