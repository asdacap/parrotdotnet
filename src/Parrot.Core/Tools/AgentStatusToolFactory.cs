using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class AgentStatusToolFactory(
    AgentResolver resolver,
    ChildRegistry children,
    ShellProcessOwners processes) : IToolFactory
{
    public ITool Create(IAgentSession session) => new AgentStatusTool(resolver, children, processes);
}
