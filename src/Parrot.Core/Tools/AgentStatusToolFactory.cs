using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class AgentStatusToolFactory(ShellProcessOwners processes) : IToolFactory
{
    public ITool Create(AgentSession session) => new AgentStatusTool(session.Resolver, session.ChildRegistry, processes);
}
