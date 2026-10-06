namespace Parrot.Agent;

/// <summary>Selects conversation inheritance independently of the child's owning parent.</summary>
internal abstract record AgentHistorySource
{
    private AgentHistorySource()
    {
    }

    /// <summary>Inherits from the owning parent at the requested launch boundary.</summary>
    public sealed record Parent : AgentHistorySource;

    /// <summary>Inherits a registered sibling's safe history, falling back to the parent only when history is absent.</summary>
    public sealed record Sibling(IAgentSession Session) : AgentHistorySource;
}
