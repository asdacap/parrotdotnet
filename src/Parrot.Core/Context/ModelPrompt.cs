using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;
using Scriban.Runtime;

namespace Parrot.Context;

internal sealed class ModelPrompt(IReadOnlyDictionary<string, string> augmentations, PromptTemplateCatalog templates) : ISystemPrompt
{
    private readonly Dictionary<string, string> _augmentations = new(augmentations, StringComparer.Ordinal);

    public void RenewEpoch()
    {
    }

    public string Build(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var sections = new List<string>();
        var aliases = new ScriptArray();
        foreach (var alias in selection.ResolvedModel.AliasSnapshot.Definitions.Values
            .Where(alias => alias.ModelString.Length > 0))
        {
            aliases.Add(new ScriptObject
            {
                ["name"] = alias.Name,
                ["model"] = alias.ModelString,
                ["usage"] = alias.Usage,
            });
        }

        if (aliases.Count > 0)
        {
            sections.Add(templates.RenderStructured(
                "context.model-aliases", new ScriptObject { ["aliases"] = aliases }, CancellationToken.None));
        }

        var augmentation = Augmentation(selection.ResolvedModel);
        if (!string.IsNullOrEmpty(augmentation))
        {
            sections.Add(augmentation);
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
