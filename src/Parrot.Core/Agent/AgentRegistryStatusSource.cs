namespace Parrot.Agent;

internal sealed class AgentRegistryStatusSource(IAgentRegistry registry) : IAgentStatusSource
{
    public IReadOnlyList<ActiveAgentSnapshot> ActiveSnapshot() => registry.ActiveSnapshot();
}
