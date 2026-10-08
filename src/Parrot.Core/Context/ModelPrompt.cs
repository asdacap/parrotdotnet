using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;
using Scriban.Runtime;

namespace Parrot.Context;

internal sealed class ModelPrompt(IReadOnlyDictionary<string, string> augmentations, IPromptTemplateCatalog templates) : ISystemPrompt
{
    private AliasSection? _aliasSection;

    public void RenewEpoch()
    {
    }

    public string Build(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var aliasSnapshot = selection.ResolvedModel.AliasSnapshot;
        if (_aliasSection is not { } aliasSection || !ReferenceEquals(aliasSection.Snapshot, aliasSnapshot))
        {
            aliasSection = new AliasSection(aliasSnapshot, RenderAliases(aliasSnapshot));
            _aliasSection = aliasSection;
        }

        var sections = new List<string> { aliasSection.Text };

        var augmentation = Augmentation(selection.ResolvedModel);
        if (!string.IsNullOrEmpty(augmentation))
        {
            sections.Add(augmentation);
        }

        return string.Join("\n\n", sections.Where(section => section.Length > 0));
    }

    private string RenderAliases(ModelAliasSnapshot snapshot)
    {
        var aliases = new ScriptArray();
        foreach (var alias in snapshot.Definitions.Values.Where(alias => alias.ModelString.Length > 0))
        {
            aliases.Add(new ScriptObject
            {
                ["name"] = alias.Name,
                ["model"] = alias.ModelString,
                ["usage"] = alias.Usage,
            });
        }

        return aliases.Count > 0
            ? templates.RenderStructured("context.model-aliases", new ScriptObject { ["aliases"] = aliases }, CancellationToken.None)
            : string.Empty;
    }

    private string? Augmentation(ResolvedModelSelection selection)
    {
        if (selection.Alias?.AugmentSystemPrompt is { } aliasAugmentation)
        {
            return aliasAugmentation;
        }

        if (augmentations.TryGetValue(selection.CanonicalModel.Selector, out var exact))
        {
            return exact;
        }

        return augmentations.GetValueOrDefault(selection.CanonicalBase);
    }

    private sealed record AliasSection(ModelAliasSnapshot Snapshot, string Text);
}
