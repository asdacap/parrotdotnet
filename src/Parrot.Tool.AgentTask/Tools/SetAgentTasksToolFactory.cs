using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;

namespace Parrot.Tools;

internal sealed class SetAgentTasksToolFactory(
    ToolWorkspace workspace,
    IAgentTaskService agentTasks,
    IPromptTemplateCatalog promptTemplates,
    ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("set_agent_tasks");

    public ITool Create(IAgentSession session) => new SetAgentTasksTool(workspace, agentTasks, promptTemplates);
}
