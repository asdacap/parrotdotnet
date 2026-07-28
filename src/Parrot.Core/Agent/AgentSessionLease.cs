namespace Parrot.Agent;

internal sealed class AgentSessionLease(AgentSession session) : IAgentSessionLease
{
    public AgentSession Session { get; } = session;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
