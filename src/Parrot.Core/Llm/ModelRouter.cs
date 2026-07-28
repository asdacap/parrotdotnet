namespace Parrot.Llm;

internal sealed class ModelRouter(
    ProviderRegistry registry,
    ModelAliasCatalog aliases,
    string configuredDefaultSelector)
{
    public ResolvedModelSelection Resolve(string selector)
    {
        var snapshot = aliases.Capture();
        var requested = selector.Length > 0 ? selector : configuredDefaultSelector;

        if (requested.Length == 0)
        {
            var canonicalDefault = registry.ResolveDefaultCanonical();
            return new(new(canonicalDefault.Selector), null, canonicalDefault, snapshot);
        }

        var requestedSelector = new ModelSelector(requested);

        var alias = snapshot.Find(requested);

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
