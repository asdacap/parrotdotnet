namespace Parrot.Store;

/// <summary>Maintains an agent's on-disk history projection.</summary>
internal interface IAgentHistoryFile
{
    string Path { get; }

    void Refresh(IEventRepository repository, string sessionId);

    void ValidateSession(string sessionId);

    void ReplaceEntries(IReadOnlyList<AgentHistoryEntry> entries);

    /// <summary>Reads and replaces history while holding the projection's synchronization gate.</summary>
    void ReplaceFromReader(Func<IReadOnlyList<AgentHistoryEntry>> readEntries);
}
