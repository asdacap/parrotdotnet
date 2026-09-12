using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.AgentTasks;

internal sealed record AgentTaskRunRequest(
    string RunId,
    string DisplayName,
    AgentTaskArtifact Artifact,
    IModelRouter Router,
    IAgentSessionScope OwnerScope,
    AgentTurnSelection Selection,
    IAgentTaskProgress Progress,
    AgentTaskConfig Configuration,
    HistoryForkBoundary RootHistoryBoundary,
    IAgentTaskRunCompletion Completion);
