using Parrot.Agent;
using Parrot.AgentTasks;

namespace Parrot.Tools;

internal sealed class GetAgentTasksToolFactory(IAgentTaskService agentTasks, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("get_agent_tasks");

    public ITool Create(IAgentSession session) => new GetAgentTasksTool(agentTasks);
}
