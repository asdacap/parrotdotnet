using Parrot.Agent;

namespace Parrot.Context;

internal sealed class CompositeSystemPromptProvider : ISystemPromptProvider
{
    private readonly List<ISystemPromptProvider> _providers;

    public CompositeSystemPromptProvider(string key, IReadOnlyList<ISystemPromptProvider> providers)
    {
        if (!SystemPromptProviderKey.IsValid(key))
        {
            throw new ArgumentException("A system prompt provider key must be namespaced.", nameof(key));
        }

        ArgumentNullException.ThrowIfNull(providers);
        var copied = new List<ISystemPromptProvider>(providers.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);

            if (!SystemPromptProviderKey.IsValid(provider.Key))
            {
                throw new ArgumentException("A system prompt provider key must be namespaced.", nameof(providers));
            }

            if (!keys.Add(provider.Key))
            {
                throw new ArgumentException($"Duplicate system prompt provider key: {provider.Key}", nameof(providers));
            }

            copied.Add(provider);
        }

        copied.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        Key = key;
        _providers = copied;
    }

    public string Key { get; }

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var prompts = new List<ISystemPrompt>(_providers.Count);

        foreach (var provider in _providers)
        {
            var prompt = provider.Materialize(identity)
                ?? throw new InvalidOperationException($"System prompt provider {provider.Key} returned no prompt.");
            prompts.Add(prompt);
        }

        return new CompositeSystemPrompt(prompts);
    }
}
