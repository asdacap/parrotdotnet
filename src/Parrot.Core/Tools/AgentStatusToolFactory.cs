using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class AgentStatusToolFactory(
    AgentResolver resolver,
    AgentSessionParentScope parentScope,
    ShellProcessOwners processes) : IToolFactory
{
    public ITool Create(IAgentSession session) => new AgentStatusTool(resolver, parentScope, processes);
}
