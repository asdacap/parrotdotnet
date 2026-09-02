using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;

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
        var aliases = selection.ResolvedModel.AliasSnapshot.Definitions.Values
            .Where(alias => alias.ModelString.Length > 0)
            .Select(alias => templates.Render("context.model-alias", [
                new PromptTemplateArgument("name", alias.Name),
                new PromptTemplateArgument("model", alias.ModelString),
                new PromptTemplateArgument("usage", alias.Usage),
            ]))
            .ToArray();

        if (aliases.Length > 0)
        {
            sections.Add(templates.Render("context.model-aliases", [
                new PromptTemplateArgument("aliases", string.Join('\n', aliases)),
            ]));
        }

        var augmentation = Augmentation(selection.ResolvedModel);
        if (!string.IsNullOrEmpty(augmentation))
        {
            sections.Add(augmentation);
        }

        sections.Add(selection.Profile.Prompt);

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
