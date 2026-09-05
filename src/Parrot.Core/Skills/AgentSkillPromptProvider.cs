using System.Text;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;

namespace Parrot.Skills;

internal sealed class AgentSkillPromptProvider(AgentSkills skills) : ISystemPromptProvider
{
    private const int MaximumCatalogBytes = 64 * 1024;
    private const string CatalogPlaceholder = "{skills}";

    public string Key => "runtime:skills";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new SkillPrompt(skills);
    }

    internal static string Render(
        IReadOnlyList<SkillMetadata> skills,
        PromptTemplateCatalog promptTemplates)
    {
        var available = skills.Where(skill => skill.Enabled && skill.PromptVisible).ToArray();
        if (available.Length == 0)
        {
            return string.Empty;
        }

        var wrapperBytes = Encoding.UTF8.GetByteCount(promptTemplates.RenderSkills(CatalogPlaceholder))
            - Encoding.UTF8.GetByteCount(CatalogPlaceholder);
        var listingBudget = MaximumCatalogBytes - wrapperBytes;
        if (listingBudget <= 0)
        {
            throw new InvalidDataException("The skills prompt template exceeds the 64 KiB catalogue limit.");
        }

        var listing = new StringBuilder();
        var entryEnds = new List<int>();
        var consumedBytes = 0;
        var omitted = 0;
        foreach (var skill in available)
        {
            var entry = $"- ${skill.Name}: {skill.EffectiveDescription} ({skill.DiscoveryPath})\n";
            var entryBytes = Encoding.UTF8.GetByteCount(entry);
            if (consumedBytes + entryBytes > listingBudget)
            {
                omitted++;
                continue;
            }

            _ = listing.Append(entry);
            entryEnds.Add(listing.Length);
            consumedBytes += entryBytes;
        }

        if (omitted > 0)
        {
            var diagnostic = $"- {omitted} additional skill(s) omitted because the catalogue reached its 64 KiB limit.\n";
            var diagnosticBytes = Encoding.UTF8.GetByteCount(diagnostic);
            while (entryEnds.Count > 0 && consumedBytes + diagnosticBytes > listingBudget)
            {
                var removedStart = entryEnds.Count == 1 ? 0 : entryEnds[^2];
                consumedBytes -= Encoding.UTF8.GetByteCount(listing.ToString(removedStart, listing.Length - removedStart));
                listing.Length = removedStart;
                entryEnds.RemoveAt(entryEnds.Count - 1);
                omitted++;
                diagnostic = $"- {omitted} additional skill(s) omitted because the catalogue reached its 64 KiB limit.\n";
                diagnosticBytes = Encoding.UTF8.GetByteCount(diagnostic);
            }

            _ = listing.Append(diagnostic);
        }

        return promptTemplates.RenderSkills(listing.ToString().TrimEnd());
    }
}
