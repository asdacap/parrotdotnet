using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

/// <summary>Tracks shared runtime usage and statistics for agents in a user session.</summary>
internal interface IUserSessionStatistics
{
    AgentSessionStatistics GetAgentStatistics(string sessionId);

    SessionUsageSnapshot CaptureUsage(string rootSessionId);

    /// <summary>Commits a usage fact, applies its increment, and publishes updated snapshots once.</summary>
    void RecordUsage(
        IEventRepository repository,
        IEventBroker broker,
        Event fact,
        AgentUsageIncrement increment,
        Action<AgentUsageIncrement> apply,
        string rootSessionId);
}
