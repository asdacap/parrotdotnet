using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;
using ProtocolSkill = Parrot.Protocol.Skill;

namespace Parrot.Cli.Enhanced;

internal sealed class SkillCompletion(GeneratedParrot.ParrotClient client)
{
    private static readonly HashSet<string> EnvironmentNames = new(StringComparer.Ordinal)
    {
        "HOME", "PATH", "USER", "SHELL", "PWD", "TMP", "TMPDIR", "TEMP", "LANG", "TERM", "XDG_CONFIG_HOME",
    };

    private List<ProtocolSkill> _available = [];
    private List<ProtocolSkill> _matches = [];
    private SkillToken? _token;

    public IReadOnlyList<ProtocolSkill> Skills => _matches;

    public int Selected { get; private set; }

    public static string Description(ProtocolSkill skill) => skill.HasShortDescription && skill.ShortDescription.Length > 0
        ? skill.ShortDescription
        : skill.Description;

    public async Task RefreshCatalog(string userSessionId, CancellationToken cancellationToken)
    {
        try
        {
            var listed = await client.ListSkillsAsync(
                new ListSkillsRequest { UserSessionId = userSessionId },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _available = [.. listed.Skills
                .Where(skill => skill.Enabled)
                .DistinctBy(skill => skill.Name, StringComparer.Ordinal)
                .OrderBy(skill => skill.Name, StringComparer.Ordinal)
                .ThenBy(skill => skill.Path, StringComparer.Ordinal)
                .Select(skill => skill.Clone())];
        }
        catch (RpcException)
        {
            _available = [];
        }

        _matches = [];
        _token = null;
        Selected = 0;
    }

    public void Refresh(PromptValue prompt)
    {
        var selected = _matches.Count == 0 ? null : _matches[Selected];
        _token = FindToken(prompt);
        _matches = _token is { } token
            ? [.. _available.Where(skill => skill.Name.StartsWith(token.Prefix, StringComparison.OrdinalIgnoreCase))]
            : [];
        Selected = selected is null ? 0 : _matches.FindIndex(skill =>
            string.Equals(skill.Path, selected.Path, StringComparison.Ordinal));
        if (Selected < 0)
        {
            Selected = 0;
        }
    }

    public void SelectPrevious()
    {
        if (_matches.Count > 0)
        {
            Selected = (Selected - 1 + _matches.Count) % _matches.Count;
        }
    }

    public void SelectNext()
    {
        if (_matches.Count > 0)
        {
            Selected = (Selected + 1) % _matches.Count;
        }
    }

    public bool Accept(IncrementalEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (_matches.Count == 0 || _token is null)
        {
            return false;
        }

        editor.ReplaceRange(_token.Start, _token.Length, $"${_matches[Selected].Name}");
        return true;
    }

    private static SkillToken? FindToken(PromptValue prompt)
    {
        var runes = prompt.Text.EnumerateRunes().ToArray();
        var cursor = Math.Clamp(prompt.Cursor, 0, runes.Length);
        var start = cursor;
        while (start > 0 && IsName(runes[start - 1]))
        {
            start--;
        }

        if (start == 0 || runes[start - 1].Value != '$')
        {
            return null;
        }

        var dollar = start - 1;
        if (dollar > 0 && IsBoundaryName(runes[dollar - 1]))
        {
            return null;
        }

        var end = cursor;
        while (end < runes.Length && IsName(runes[end]))
        {
            end++;
        }

        var prefix = string.Concat(runes[start..cursor]);
        var token = string.Concat(runes[start..end]);
        if (EnvironmentNames.Contains(token))
        {
            return null;
        }

        return new SkillToken(dollar, end - dollar, prefix);
    }

    private static bool IsName(System.Text.Rune rune) => rune.Value is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9'
        or '_'
        or '-';

    private static bool IsBoundaryName(System.Text.Rune rune) => rune.Value is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9'
        or '_';

    private sealed record SkillToken(int Start, int Length, string Prefix);
}
