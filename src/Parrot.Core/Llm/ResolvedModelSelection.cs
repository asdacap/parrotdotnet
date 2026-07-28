namespace Parrot.Llm;

internal sealed record ResolvedModelSelection(
    ModelSelector RequestedSelector,
    ModelAliasDefinition? Alias,
    ProviderModel CanonicalModel,
    ModelAliasSnapshot AliasSnapshot)
{
    public string CanonicalBase => $"{CanonicalModel.Provider.Id}/{CanonicalModel.Model.Id}";
}
