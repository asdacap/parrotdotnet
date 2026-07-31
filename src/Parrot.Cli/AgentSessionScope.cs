using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Cli;

internal sealed class AgentSessionScope(AgentSession session, AgentQueues queues) : IAgentSessionLease
{
    public AgentSession Session { get; } = session;

    public ValueTask DisposeAsync()
    {
        queues.Dispose();
        return ValueTask.CompletedTask;
    }
}
