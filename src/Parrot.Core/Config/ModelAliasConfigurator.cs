using Parrot.Llm;

namespace Parrot.Config;

internal sealed class ModelAliasConfigurator(Configuration configuration, ModelAliasCatalog catalog)
{
    private readonly Lock _configureLock = new();

    public ModelAliasSnapshot Capture() => catalog.Capture();

    public ModelAliasDefinition Configure(string name, string modelString)
    {
        lock (_configureLock)
        {
            var current = catalog.Capture();
            var existing = current.Find(name)
                ?? throw new LLMProviderException($"model alias: alias \"{name}\" is not defined");
            var updated = existing with { ModelString = modelString };
            var replacement = catalog.PrepareReplacement(current.Definitions.Values.Select(definition =>
                string.Equals(definition.Name, name, StringComparison.Ordinal) ? updated : definition));

            configuration.SetModelAlias(name, modelString);
            catalog.Publish(replacement);
            return updated;
        }
    }

    public IReadOnlyList<ProviderModelAliasDefaults> ListProviderDefaults(IEnumerable<string> providerIds)
    {
        ArgumentNullException.ThrowIfNull(providerIds);
        return
        [
            .. providerIds
                .Distinct(StringComparer.Ordinal)
                .Where(configuration.ProviderModelAliasDefaults.ContainsKey)
                .OrderBy(providerId => providerId, StringComparer.Ordinal)
                .Select(providerId => configuration.ProviderModelAliasDefaults[providerId]),
        ];
    }

    public IReadOnlyList<ModelAliasDefinition> ApplyProviderDefaults(string providerId)
    {
        lock (_configureLock)
        {
            var defaults = configuration.ProviderModelAliasDefaults.GetValueOrDefault(providerId)
                ?? throw new LLMProviderException(
                    $"model alias defaults: provider \"{providerId}\" is not configured");
            var targets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["low_llm"] = defaults.LowModelString,
                ["medium_llm"] = defaults.MediumModelString,
                ["high_llm"] = defaults.HighModelString,
                ["xhigh_llm"] = defaults.XHighModelString,
            };
            var current = catalog.Capture();
            var updated = current.Definitions.Values.Select(definition =>
                targets.TryGetValue(definition.Name, out var modelString)
                    ? definition with { ModelString = modelString }
                    : definition).ToArray();
            var replacement = catalog.PrepareReplacement(updated);

            configuration.SetModelAliases(defaults);
            catalog.Publish(replacement);
            return [.. replacement.Definitions.Values.Where(definition => targets.ContainsKey(definition.Name))];
        }
    }
}
