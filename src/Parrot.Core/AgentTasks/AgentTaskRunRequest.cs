using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.AgentTasks;

internal sealed record AgentTaskRunRequest(
    string RunId,
    string DisplayName,
    AgentTaskArtifact Artifact,
    ModelRouter Router,
    IAgentSessionScope OwnerScope,
    AgentTurnSelection Selection,
    AgentTaskProgress Progress,
    AgentTaskConfig Configuration,
    HistoryForkBoundary RootHistoryBoundary,
    IAgentTaskRunCompletion Completion);
