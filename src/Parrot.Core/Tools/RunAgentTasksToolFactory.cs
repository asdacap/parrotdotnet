using Parrot.Agent;
using Parrot.Llm;

namespace Parrot.Tools;

internal sealed class RunAgentTasksToolFactory(
    ToolWorkspace workspace,
    AgentRegistry agents,
    ModelRouter router) : IToolFactory
{
    public ITool Create(AgentSession session) =>
        new RunAgentTasksTool(workspace, agents, router, session);
}
