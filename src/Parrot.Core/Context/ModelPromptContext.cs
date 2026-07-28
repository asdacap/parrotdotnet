using Parrot.Agent;
using Parrot.Llm;

namespace Parrot.Context;

internal sealed class ModelPromptContext(IReadOnlyDictionary<string, string> augmentations)
{
    private readonly Dictionary<string, string> _augmentations =
        new(augmentations, StringComparer.Ordinal);

    public string Build(string epochContext, AgentTurnSelection selection)
    {
        var sections = new List<string> { epochContext };
        var aliases = selection.ResolvedModel.AliasSnapshot.Definitions.Values
            .Where(alias => alias.ModelString.Length > 0)
            .Select(alias => $"- {alias.Name}: {alias.ModelString} — {alias.Usage}")
            .ToArray();

        if (aliases.Length > 0)
        {
            sections.Add(
                "Configured model aliases may be passed anywhere a model selector is accepted, especially "
                + "`agent_spawn.model`:\n"
                + string.Join('\n', aliases));
        }

        var augmentation = Augmentation(selection.ResolvedModel);
        if (!string.IsNullOrEmpty(augmentation))
        {
            sections.Add(augmentation);
        }

        if (selection.Profile is not null)
        {
            sections.Add(selection.Profile.Prompt);
            sections.Add(selection.Profile.HardRule);
        }

        return string.Join("\n\n", sections.Where(section => section.Length > 0));
    }

    private string? Augmentation(ResolvedModelSelection selection)
    {
        if (selection.Alias?.AugmentSystemPrompt is { } aliasAugmentation)
        {
            return aliasAugmentation;
        }

        if (_augmentations.TryGetValue(selection.CanonicalModel.Selector, out var exact))
        {
            return exact;
        }

        return _augmentations.GetValueOrDefault(selection.CanonicalBase);
    }
}
