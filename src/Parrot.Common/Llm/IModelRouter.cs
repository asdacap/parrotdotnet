namespace Parrot.Llm;

/// <summary>Resolves model selectors against configured providers and a captured routing revision.</summary>
internal interface IModelRouter
{
    long RoutingRevision { get; }

    ResolvedModelSelection Resolve(string selector);

    /// <summary>Resolves using the supplied routing snapshot instead of capturing a new one.</summary>
    ResolvedModelSelection ResolveFrom(ModelRoutingSnapshot snapshot, string selector);
}
