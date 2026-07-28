namespace Parrot.Agent;

internal interface IAgentSessionLease : IAsyncDisposable
{
    AgentSession Session { get; }
}
