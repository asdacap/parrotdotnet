namespace Parrot.Llm;

internal sealed class ModelRouting(ModelAliasCatalog aliasCatalog, string configuredDefaultSelector)
{
    private ModelRoutingSnapshot _snapshot = new(configuredDefaultSelector, aliasCatalog.InitialSnapshot, 0);

    public ModelRoutingSnapshot Capture() => Volatile.Read(ref _snapshot);

    public ModelRoutingSnapshot Prepare(string configuredDefault, IEnumerable<ModelAliasDefinition> aliases)
    {
        ArgumentNullException.ThrowIfNull(configuredDefault);
        return PrepareWithRevision(configuredDefault, aliases, Capture().Revision + 1);
    }

    public ModelRoutingSnapshot PrepareWithRevision(
        string configuredDefault,
        IEnumerable<ModelAliasDefinition> aliases,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(configuredDefault);
        return new(configuredDefault, aliasCatalog.PrepareReplacement(aliases), revision);
    }

    public void Publish(ModelRoutingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _snapshot, snapshot);
    }
}
