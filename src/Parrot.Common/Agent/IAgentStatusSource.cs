namespace Parrot.Agent;

/// <summary>Provides snapshots of active agents without transferring ownership of their sessions.</summary>
internal interface IAgentStatusSource
{
    IReadOnlyList<ActiveAgentSnapshot> ActiveSnapshot();
}
