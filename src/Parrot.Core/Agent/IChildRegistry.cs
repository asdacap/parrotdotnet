namespace Parrot.Agent;

/// <summary>Owns direct child scopes and resolves descendants within the owning agent's subtree.</summary>
internal interface IChildRegistry
{
    bool IsAccepting { get; }

    /// <summary>Returns the direct child with the canonical session id, or null when absent or shutting down.</summary>
    IAgentSessionScope? FindDirectChildScope(string childSessionId);

    /// <summary>
    /// Removes the exact registered scope and transfers disposal responsibility to the caller.
    /// Returns null if shutdown already took ownership; otherwise a missing or mismatched scope throws.
    /// </summary>
    IAgentSessionScope? DetachDirectChildScope(IAgentSessionScope scope);

    /// <summary>Resolves a canonical id before a child name; throws when absent or shutting down.</summary>
    IAgentSessionScope ResolveDirectChildScope(string sessionIdOrName);

    /// <summary>Searches accepting child registries recursively by canonical id, returning null when absent.</summary>
    IAgentSessionScope? FindDescendantScope(string sessionId);

    /// <summary>Checks scope identity within accepting descendant registries.</summary>
    bool ContainsDescendantScope(IAgentSessionScope candidate);

    /// <summary>Returns a parent-before-descendants snapshot, excluding registries already shutting down.</summary>
    IReadOnlyList<IAgentSession> SnapshotDescendants();

    /// <summary>Resolves a direct child's name only; throws when absent or shutting down.</summary>
    IAgentSessionScope ResolveNamedChildScope(string name);

    /// <summary>
    /// Takes ownership on success; returns false during shutdown.
    /// A different parent or duplicate session id or name throws without retaining the supplied scope.
    /// </summary>
    bool TryAdd(IAgentSessionScope scope);
}
