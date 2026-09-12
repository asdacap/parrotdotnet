namespace Parrot.Agent;

/// <summary>Resolves agent session recipients within the owning agent's permitted topology.</summary>
internal interface IAgentResolver
{
    /// <summary>Resolves a status target scope by canonical session id or name.</summary>
    IAgentSessionScope ResolveStatusTargetScope(string sessionIdOrName);

    /// <summary>Resolves a status target by canonical session id or name.</summary>
    IAgentSession ResolveStatusTarget(string sessionIdOrName);

    /// <summary>Resolves a permitted parent or child recipient by canonical session id, name, or descendant path.</summary>
    IAgentSession ResolveRecipient(string sessionIdOrName);
}
