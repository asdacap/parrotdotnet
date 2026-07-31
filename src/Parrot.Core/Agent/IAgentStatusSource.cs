namespace Parrot.Agent;

internal interface IAgentStatusSource
{
    IReadOnlyList<ActiveAgentSnapshot> ActiveSnapshot();
}
