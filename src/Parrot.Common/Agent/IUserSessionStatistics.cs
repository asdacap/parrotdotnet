using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

/// <summary>Tracks shared runtime usage and statistics for agents in a user session.</summary>
internal interface IUserSessionStatistics
{
    /// <summary>Returns the agent's live counters, seeded from replayed history when present.</summary>
    AgentSessionStatistics GetAgentStatistics(string sessionId);

    /// <summary>Captures the user session's usage from the root agent's cumulative statistics.</summary>
    SessionUsageSnapshot CaptureUsage(IAgentSession root);

    /// <summary>Commits a usage fact, applies its increment, and publishes the agent's and the root's updated snapshots once.</summary>
    void RecordUsage(
        IEventRepository repository,
        IEventBroker broker,
        Event fact,
        AgentUsageIncrement increment,
        Action<AgentUsageIncrement> apply,
        IAgentSession agent,
        IAgentSession root);
}
