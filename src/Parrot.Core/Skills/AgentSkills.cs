using System.Text;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Skills;

internal sealed class AgentSkills(
    SkillCatalog catalog,
    PromptTemplateCatalog promptTemplates)
{
    private const int MaximumSkillBytes = 1024 * 1024;
    private const int MaximumTurnBytes = 1024 * 1024;
    private const int MaximumDiagnosticLength = 1024;
    private readonly List<SelectedSkill> _selected = [];
    private readonly HashSet<string> _selectedPaths = new(PathComparer());
    private SkillSnapshot _snapshot = SkillSnapshot.Empty;
    private SecurityProfile? _turnSecurity;

    public bool HasSelection => _selected.Count > 0;

    public void BeginTurn(SecurityProfile security)
    {
        ArgumentNullException.ThrowIfNull(security);
        _turnSecurity = security;
        _snapshot = catalog.Capture(security);
        _selected.Clear();
        _selectedPaths.Clear();
    }

    public string BuildCatalog(SecurityProfile security)
    {
        ArgumentNullException.ThrowIfNull(security);
        var snapshot = _turnSecurity is null ? catalog.Capture(security) : SnapshotForTurn(security);
        return AgentSkillPromptProvider.Render(snapshot.Skills, promptTemplates);
    }

    public void Select(IReadOnlyList<ConversationPart> parts, SecurityProfile security)
    {
        ArgumentNullException.ThrowIfNull(parts);
        EnsureEpoch(security);
        var positionOffset = 0;
        var mentions = new List<SkillMention>();
        foreach (var part in parts)
        {
            if (part.Kind != ConversationPartKind.Text)
            {
                continue;
            }

            mentions.AddRange(SkillMentionParser.Parse(part.Text)
                .Select(mention => mention with { Position = mention.Position + positionOffset }));
            positionOffset += part.Text.Length;
        }

        foreach (var mention in mentions.OrderBy(mention => mention.Position))
        {
            var skill = mention.Path is null
                ? _snapshot.Skills.FirstOrDefault(candidate =>
                    candidate.Enabled && string.Equals(candidate.Name, mention.Name, StringComparison.Ordinal))
                : _snapshot.Skills.FirstOrDefault(candidate =>
                    candidate.Enabled
                    && string.Equals(candidate.Name, mention.Name, StringComparison.Ordinal)
                    && (PathsEqual(candidate.Path, mention.Path) || PathsEqual(candidate.DiscoveryPath, mention.Path)));
            if (skill is not null && _selectedPaths.Add(skill.Path))
            {
                _selected.Add(new(skill, mention.Position));
            }
        }
    }

    public void AppendTo(List<LLMMessage> messages, SecurityProfile security)
    {
        ArgumentNullException.ThrowIfNull(messages);
        EnsureEpoch(security);
        if (_selected.Count == 0)
        {
            return;
        }

        var rendered = RenderSelection(security);
        if (rendered.Length > 0)
        {
            messages.Add(LLMMessage.User(rendered));
        }
    }

    public IReadOnlyList<LLMMessage> Augment(
        IReadOnlyList<LLMMessage> history,
        SecurityProfile security)
    {
        ArgumentNullException.ThrowIfNull(history);
        var augmented = new List<LLMMessage>(history);
        AppendTo(augmented, security);
        return augmented;
    }

    public void EndTurn()
    {
        _selected.Clear();
        _selectedPaths.Clear();
        _turnSecurity = null;
        _snapshot = SkillSnapshot.Empty;
    }

    private static bool PathsEqual(string left, string? right)
    {
        if (right is null)
        {
            return false;
        }

        try
        {
            return PathComparer().Equals(PlatformPath.Normalize(left), PlatformPath.Normalize(right));
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static void Append(StringBuilder rendered, string content)
    {
        if (rendered.Length > 0)
        {
            _ = rendered.AppendLine().AppendLine();
        }

        _ = rendered.Append(content);
    }

    private void EnsureEpoch(SecurityProfile security)
    {
        ArgumentNullException.ThrowIfNull(security);
        if (_turnSecurity is null)
        {
            BeginTurn(security);
            return;
        }

        _ = SnapshotForTurn(security);
    }

    private SkillSnapshot SnapshotForTurn(SecurityProfile security) => ReferenceEquals(_turnSecurity, security)
        ? _snapshot
        : throw new InvalidOperationException("The active skill snapshot belongs to a different security profile.");

    private string RenderSelection(SecurityProfile security)
    {
        var rendered = new StringBuilder();
        var consumedBytes = 0;
        var omittedDiagnostics = 0;
        var summaryReserveBytes = Encoding.UTF8.GetByteCount(RenderOmittedDiagnostics(int.MaxValue)) + 2;
        var contentBudget = MaximumTurnBytes - summaryReserveBytes;
        if (contentBudget <= 0)
        {
            throw new InvalidDataException("The unavailable-skill prompt template exceeds the active-turn prompt budget.");
        }

        foreach (var selection in _selected
                     .OrderBy(item => item.Position)
                     .ThenBy(item => item.Skill.Path, PathComparer()))
        {
            string content;
            try
            {
                content = SkillFileReader.Read(
                    selection.Skill.DiscoveryPath,
                    selection.Skill.Path,
                    security);
            }
            catch (Exception failure) when (failure is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException)
            {
                AppendDiagnostic(
                    rendered,
                    selection.Skill.Name,
                    failure.Message,
                    contentBudget,
                    ref consumedBytes,
                    ref omittedDiagnostics);
                continue;
            }

            var selected = promptTemplates.RenderSelectedSkill(
                Escape(selection.Skill.Name),
                Escape(selection.Skill.DiscoveryPath),
                content);
            var separatorBytes = rendered.Length > 0 ? 2 : 0;
            var selectedBytes = Encoding.UTF8.GetByteCount(selected);
            if (selectedBytes > MaximumSkillBytes || selectedBytes + separatorBytes > contentBudget - consumedBytes)
            {
                AppendDiagnostic(
                    rendered,
                    selection.Skill.Name,
                    "selected skill instructions exceed the active-turn prompt budget",
                    contentBudget,
                    ref consumedBytes,
                    ref omittedDiagnostics);
                continue;
            }

            Append(rendered, selected);
            consumedBytes += selectedBytes + separatorBytes;
        }

        if (omittedDiagnostics > 0)
        {
            var summary = RenderOmittedDiagnostics(omittedDiagnostics);
            var separatorBytes = rendered.Length > 0 ? 2 : 0;
            Append(rendered, summary);
            var finalBytes = consumedBytes + Encoding.UTF8.GetByteCount(summary) + separatorBytes;
            if (finalBytes > MaximumTurnBytes)
            {
                throw new InvalidDataException("The selected-skill context exceeds the active-turn prompt budget.");
            }
        }

        return rendered.ToString();
    }

    private string RenderOmittedDiagnostics(int count) =>
        promptTemplates.RenderUnavailableSkill("selected skills", $"{count} diagnostics omitted by the active-turn prompt budget");

    private void AppendDiagnostic(
        StringBuilder rendered,
        string name,
        string message,
        int contentBudget,
        ref int consumedBytes,
        ref int omittedDiagnostics)
    {
        var normalized = string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > MaximumDiagnosticLength)
        {
            normalized = normalized[..MaximumDiagnosticLength];
        }

        var diagnostic = promptTemplates.RenderUnavailableSkill(Escape(name), Escape(normalized));
        var separatorBytes = rendered.Length > 0 ? 2 : 0;
        var diagnosticBytes = Encoding.UTF8.GetByteCount(diagnostic);
        if (diagnosticBytes + separatorBytes > contentBudget - consumedBytes)
        {
            omittedDiagnostics++;
            return;
        }

        Append(rendered, diagnostic);
        consumedBytes += diagnosticBytes + separatorBytes;
    }

    private sealed record SelectedSkill(SkillMetadata Skill, int Position);
}
