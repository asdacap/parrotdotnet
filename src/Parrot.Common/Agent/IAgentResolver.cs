namespace Parrot.Agent;

/// <summary>Resolves agent session recipients within the owning agent's permitted topology.</summary>
internal interface IAgentResolver
{
    /// <summary>Resolves a direct child scope by name.</summary>
    IAgentSessionScope ResolveStatusTargetScope(string name);

    /// <summary>Resolves a direct child by name.</summary>
    IAgentSession ResolveStatusTarget(string name);

    /// <summary>Resolves the parent by its name or the literal "parent", a direct child by name, or a descendant by slash-separated name path.</summary>
    IAgentSession ResolveRecipient(string nameOrPath);
}
