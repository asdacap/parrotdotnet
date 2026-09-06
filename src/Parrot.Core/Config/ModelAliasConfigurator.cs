using Parrot.Llm;

namespace Parrot.Config;

internal sealed class ModelAliasConfigurator(ModelConfigurationCoordinator models)
{
    public ModelAliasSnapshot Capture() => models.CaptureRouting().Aliases;

    public ModelAliasDefinition Configure(string name, string modelString) =>
        models.ConfigureAlias(name, modelString);

    public IReadOnlyList<ProviderModelAliasDefaults> ListProviderDefaults(IEnumerable<string> providerIds) =>
        models.ListProviderDefaults(providerIds);

    public IReadOnlyList<ModelAliasDefinition> ApplyProviderDefaults(string providerId) =>
        models.ApplyProviderDefaults(providerId);
}
