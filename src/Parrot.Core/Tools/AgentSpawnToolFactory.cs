using Parrot.Agent;
using Parrot.Llm;

namespace Parrot.Tools;

internal sealed class AgentSpawnToolFactory(
    ChildRegistry children,
    ModelRouter router) : IToolFactory
{
    public ITool Create(AgentSession session) =>
        new AgentSpawnTool(children, router, session);
}
