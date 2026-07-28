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
}
