using System.Text;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Skills;

internal sealed class AgentSkills(
    ISkillCatalog catalog,
    IPromptTemplateCatalog promptTemplates)
{
    private const int MaximumSkillBytes = 1024 * 1024;
    private const int MaximumTurnBytes = 1024 * 1024;
    private const int MaximumDiagnosticLength = 1024;
    private readonly List<SelectedSkill> _selected = [];
    private readonly HashSet<string> _selectedPaths = new(PathComparer());
    private readonly HashSet<string> _reportedPaths = new(PathComparer());
    private SkillSnapshot _snapshot = SkillSnapshot.Empty;
    private bool _turnActive;

    public bool HasSelection => _selected.Count > 0;

    public void BeginTurn()
    {
        _turnActive = true;
        _snapshot = catalog.Capture();
        _selected.Clear();
        _selectedPaths.Clear();
        _reportedPaths.Clear();
    }

    public string BuildCatalog()
    {
        var snapshot = _turnActive ? _snapshot : catalog.Capture();
        return AgentSkillPromptProvider.Render(snapshot.Skills, promptTemplates);
    }

    public void Select(IReadOnlyList<ConversationPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        EnsureTurn();
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

    public IReadOnlyList<string> AppendTo(List<LLMMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        EnsureTurn();
        if (_selected.Count == 0)
        {
            return [];
        }

        var rendering = RenderSelection();
        if (rendering.Content.Length > 0)
        {
            messages.Add(LLMMessage.User(rendering.Content));
        }

        return [.. rendering.LoadedPaths.Where(_reportedPaths.Add)];
    }

    public IReadOnlyList<LLMMessage> Augment(IReadOnlyList<LLMMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        EnsureTurn();
        var augmented = new List<LLMMessage>(history);
        if (_selected.Count == 0)
        {
            return augmented;
        }

        var rendering = RenderSelection();
        if (rendering.Content.Length > 0)
        {
            augmented.Add(LLMMessage.User(rendering.Content));
        }

        return augmented;
    }

    public void EndTurn()
    {
        _selected.Clear();
        _selectedPaths.Clear();
        _reportedPaths.Clear();
        _turnActive = false;
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

    private void EnsureTurn()
    {
        if (!_turnActive)
        {
            BeginTurn();
        }
    }

    private SkillRendering RenderSelection()
    {
        var rendered = new StringBuilder();
        var loadedPaths = new List<string>();
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
                    selection.Skill.Path);
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
            loadedPaths.Add(selection.Skill.DiscoveryPath);
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

        return new SkillRendering(rendered.ToString(), loadedPaths);
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

    private sealed record SkillRendering(string Content, IReadOnlyList<string> LoadedPaths);
}
