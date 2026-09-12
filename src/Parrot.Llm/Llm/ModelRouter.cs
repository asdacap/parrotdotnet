namespace Parrot.Llm;

internal sealed class ModelRouter(ProviderRegistry registry, ModelRouting routing) : IModelRouter
{
    public long RoutingRevision => routing.Capture().Revision;

    public ResolvedModelSelection Resolve(string selector) => ResolveFrom(routing.Capture(), selector);

    public ResolvedModelSelection ResolveFrom(ModelRoutingSnapshot snapshot, string selector)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var requested = selector.Length > 0 ? selector : snapshot.ConfiguredDefaultSelector;

        if (requested.Length == 0)
        {
            var canonicalDefault = registry.ResolveDefaultCanonical();
            return new(new(canonicalDefault.Selector), null, canonicalDefault, snapshot);
        }

        var requestedSelector = new ModelSelector(requested);
        var alias = snapshot.Aliases.Find(requested);

        if (alias is null)
        {
            return new(requestedSelector, null, registry.ResolveCanonical(requested), snapshot);
        }

        if (alias.ModelString.Length == 0)
        {
            throw new LLMProviderException($"model alias: alias \"{requested}\" is not configured");
        }

        return new(requestedSelector, alias, registry.ResolveCanonical(alias.ModelString), snapshot);
    }
}
