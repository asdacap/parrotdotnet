namespace Parrot.Store;

/// <summary>Maintains an agent's on-disk history projection.</summary>
internal interface IAgentHistoryFile
{
    string Path { get; }

    /// <summary>Brings the projection up to date, appending only entries added since the last write.</summary>
    void Refresh(IEventRepository repository, string sessionId);

    /// <summary>Rewrites the whole projection, for history that changed other than by appending.</summary>
    void Rebuild(IEventRepository repository, string sessionId);

    void ValidateSession(string sessionId);

    void ReplaceEntries(IReadOnlyList<AgentHistoryEntry> entries);
}
