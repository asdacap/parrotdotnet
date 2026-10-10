using Parrot.Agent;
using Parrot.Protocol;

namespace Parrot.Store;

/// <summary>Commits user-session events and maintains the session-wide durable projections.</summary>
internal interface IEventRepository
{
    IUserSessionStatistics GetRuntimeStatistics();

    /// <summary>Binds one agent's history, keeping its on-disk projection in step with every write.</summary>
    IAgentHistory BindAgentHistory(string agentSessionId, IAgentHistoryFile historyFile);

    SessionUsage? Append(Event published, string? messageRole, string? messageContent);

    IReadOnlyList<Event> Replay();

    IReadOnlyList<string> AgentHistorySessionIds();

    /// <summary>Lists every agent that ever started, by its first start, with its parent and name.</summary>
    IReadOnlyList<AgentLineageRecord> AgentLineage();

    IReadOnlyList<AgentHistoryEntry> AgentHistory(string agentSessionId);

    /// <summary>Lists the history entries recorded after both cursors, in projection order.</summary>
    IReadOnlyList<AgentHistoryEntry> AgentHistoryAfter(string agentSessionId, long historySequence, long requestEventSequence);

    (string AgentSessionId, string Mode) SessionState(string userSessionId, string requestedMode);

    void UpdateMode(string userSessionId, string agentSessionId, string mode);

    ImageArtifactMetadata RecordImageArtifact(ImageArtifactMetadata artifact, string uploadId);

    ImageArtifactMetadata? ResolveImageArtifact(string artifactId);

    void ClaimImageArtifact(string artifactId, string referenceId);

    void ReleaseImageArtifact(string artifactId, string referenceId);

    IReadOnlyList<ImageArtifactMetadata> RemoveStaleUnreferencedImageArtifacts(DateTimeOffset before);

    long AppendUsageFact(Event published);

    AgentStatisticsReplay ReplayStatistics();
}
