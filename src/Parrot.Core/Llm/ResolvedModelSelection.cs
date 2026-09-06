namespace Parrot.Llm;

internal sealed record ResolvedModelSelection(
    ModelSelector RequestedSelector,
    ModelAliasDefinition? Alias,
    ProviderModel CanonicalModel,
    ModelRoutingSnapshot RoutingSnapshot)
{
    public string CanonicalBase => $"{CanonicalModel.Provider.Id}/{CanonicalModel.Model.Id}";

    public ModelAliasSnapshot AliasSnapshot => RoutingSnapshot.Aliases;
}
