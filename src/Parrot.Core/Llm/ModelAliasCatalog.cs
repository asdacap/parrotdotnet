namespace Parrot.Llm;

internal sealed class ModelAliasCatalog
{
    private readonly ProviderRegistry _registry;
    private ModelAliasSnapshot _snapshot;

    public ModelAliasCatalog(ProviderRegistry registry, IEnumerable<ModelAliasDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(definitions);
        _registry = registry;
        _snapshot = PrepareReplacement(definitions);
    }

    public ModelAliasSnapshot Capture() => Volatile.Read(ref _snapshot);

    public ModelAliasSnapshot PrepareReplacement(IEnumerable<ModelAliasDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var copied = definitions.ToList();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in copied)
        {
            ValidateName(definition.Name);

            if (!names.Add(definition.Name))
            {
                throw new LLMProviderException($"model alias: duplicate alias \"{definition.Name}\"");
            }

            if (definition.Usage.Length == 0)
            {
                throw new LLMProviderException($"model alias: usage is required for \"{definition.Name}\"");
            }
        }

        foreach (var definition in copied.Where(definition => definition.ModelString.Length > 0))
        {
            if (names.Contains(definition.ModelString))
            {
                throw new LLMProviderException(
                    $"model alias: alias \"{definition.Name}\" targets alias \"{definition.ModelString}\"");
            }

            try
            {
                _ = _registry.ResolveCanonical(definition.ModelString);
            }
            catch (LLMProviderException failure)
            {
                throw new LLMProviderException(
                    $"model alias: invalid target for \"{definition.Name}\": {failure.Message}", failure);
            }
        }

        return new ModelAliasSnapshot(copied);
    }

    public void Publish(ModelAliasSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _snapshot, snapshot);
    }

    public void Replace(IEnumerable<ModelAliasDefinition> definitions) => Publish(PrepareReplacement(definitions));

    private static void ValidateName(string name)
    {
        if (name.Length == 0)
        {
            throw new LLMProviderException("model alias: alias name is required");
        }

        if (!string.Equals(name.Trim(), name, StringComparison.Ordinal))
        {
            throw new LLMProviderException($"model alias: alias name \"{name}\" has surrounding whitespace");
        }

        if (name.Contains('/', StringComparison.Ordinal))
        {
            throw new LLMProviderException($"model alias: alias name \"{name}\" contains '/'");
        }
    }
}
