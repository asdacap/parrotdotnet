using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;
using Scriban.Runtime;

namespace Parrot.Context;

internal sealed class ModelPrompt(ModelProfiles profiles, IModelRouter router, IPromptTemplateCatalog templates) : ISystemPrompt
{
    private AliasSection? _aliasSection;

    public void RenewEpoch()
    {
    }

    public string Build(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var routingSnapshot = selection.ResolvedModel.RoutingSnapshot;
        if (_aliasSection is not { } aliasSection || !ReferenceEquals(aliasSection.Snapshot, routingSnapshot))
        {
            aliasSection = new AliasSection(routingSnapshot, RenderAliases(routingSnapshot));
            _aliasSection = aliasSection;
        }

        var modelPrompt = profiles.Resolve(selection.ResolvedModel.CanonicalModel.Selector).SystemPrompt;
        string?[] sections = [aliasSection.Text, modelPrompt, selection.ResolvedModel.Alias?.AugmentSystemPrompt];
        return string.Join("\n\n", sections.Where(section => !string.IsNullOrEmpty(section)));
    }

    private string RenderAliases(ModelRoutingSnapshot snapshot)
    {
        var aliases = new ScriptArray();
        foreach (var alias in snapshot.Aliases.Definitions.Values.Where(alias => alias.ModelString.Length > 0))
        {
            string? modelUsage = null;
            try
            {
                var resolved = router.ResolveFrom(snapshot, alias.Name);
                modelUsage = profiles.Resolve(resolved.CanonicalModel.Selector).Usage;
            }
            catch (LLMProviderException)
            {
            }

            var usage = string.IsNullOrEmpty(modelUsage)
                ? alias.Usage
                : string.IsNullOrEmpty(alias.Usage) ? modelUsage : $"{alias.Usage}\n{modelUsage}";
            aliases.Add(new ScriptObject
            {
                ["name"] = alias.Name,
                ["model"] = alias.ModelString,
                ["usage"] = usage,
            });
        }

        return aliases.Count > 0
            ? templates.RenderStructured("context.model-aliases", new ScriptObject { ["aliases"] = aliases }, CancellationToken.None)
            : string.Empty;
    }

    private sealed record AliasSection(ModelRoutingSnapshot Snapshot, string Text);
}
