using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Llm;

namespace Parrot.Tools;

internal sealed class RunAgentTasksToolFactory(
    ToolWorkspace workspace,
    IModelRouter router,
    IAgentSessionScope ownerScope,
    IAgentTaskRunCatalog runs,
    Func<string, AgentTaskArtifact> parseArtifact,
    Func<string, IAgentTaskProgress> createProgress,
    Func<IAgentSession, IAgentTaskRunCompletion> createCompletion,
    AgentTaskConfig agentTasks) : IToolFactory
{
    public ITool Create(IAgentSession session) =>
        new RunAgentTasksTool(
            workspace,
            router,
            ownerScope,
            runs,
            createCompletion(session),
            parseArtifact,
            createProgress,
            agentTasks);
}
