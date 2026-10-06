namespace Parrot.Agent;

/// <summary>Resolves agents and resource owners within the owning agent's user session.</summary>
internal interface IAgentResolver
{
    /// <summary>Resolves a direct child name or a relative or absolute agent path for status.</summary>
    IAgentSessionScope ResolveStatusTargetScope(string name);

    /// <summary>Resolves a direct child name or a relative or absolute agent path for status.</summary>
    IAgentSession ResolveStatusTarget(string name);

    /// <summary>Resolves a parent alias, direct child name, or relative or absolute agent path.</summary>
    IAgentSession ResolveRecipient(string nameOrPath);

    /// <summary>Splits a qualified resource path at its last slash and resolves its owner, rejecting a more permissive owner.</summary>
    (IAgentSessionScope Scope, string Name) ResolveResource(string path);

    /// <summary>Reports whether the specified session is an ancestor of this agent.</summary>
    bool IsAncestor(IAgentSession session);
}
