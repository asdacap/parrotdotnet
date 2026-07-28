using Parrot.Agent;

namespace Parrot.Cli;

internal sealed class AgentSessionScope(AgentSession session) : IAgentSessionLease
{
    public AgentSession Session { get; } = session;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
