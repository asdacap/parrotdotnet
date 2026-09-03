using Parrot.Agent;
using Parrot.Llm;

namespace Parrot.Tools;

internal sealed class AgentSpawnToolFactory(ModelRouter router) : IToolFactory
{
    public ITool Create(AgentSession session) =>
        new AgentSpawnTool(session.ChildRegistry, router, session);
}
