using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class AgentStatusToolFactory(
    AgentRegistry agents,
    ShellProcessOwners processes) : IToolFactory
{
    public ITool Create(AgentSession session) => new AgentStatusTool(agents, processes, session);
}
