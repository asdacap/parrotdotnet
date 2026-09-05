using Parrot.Protocol;

namespace Parrot.AgentTasks;

internal sealed record AgentTaskRunSnapshot(
    string OwnerAgentSessionId,
    string RunId,
    string DisplayName,
    AgentTaskProgressSnapshot Progress);
