using System.Collections.Immutable;
using Parrot.Agent;

namespace Parrot.Store;

internal sealed record AgentStatisticsReplay(
    long Revision,
    ImmutableDictionary<string, AgentSessionStatisticsSnapshot> Agents,
    ImmutableDictionary<string, string> Parents,
    ImmutableHashSet<string> IncompleteLegacyToolCounts);
