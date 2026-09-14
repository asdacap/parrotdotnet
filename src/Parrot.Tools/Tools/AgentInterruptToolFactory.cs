using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentInterruptToolFactory(IChildRegistry registry) : IToolFactory
{
    public ITool Create(IAgentSession session) => new AgentInterruptTool(registry);
}
