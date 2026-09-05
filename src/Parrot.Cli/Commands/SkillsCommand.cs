using Grpc.Core;
using Parrot.Protocol;
using ProtocolSkill = Parrot.Protocol.Skill;

namespace Parrot.Cli.Commands;

internal sealed class SkillsCommand(
    ISlashSession session,
    ISlashActivity activity,
    ISlashDialog dialog,
    Func<CancellationToken, Task> refreshCompletion) : ISlashCommand
{
    private const string ListId = "list";
    private const string ManageId = "manage";

    public string Name => "/skills";

    public string Summary => "List, enable, or disable skills";

    public async Task Run(string arguments, CancellationToken cancellationToken)
    {
        if (arguments.Length > 0)
        {
            await dialog.ShowError("usage: /skills", cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var action = await dialog.Select(
                "Skills",
                [
                    new SlashDialogOption(ListId, "List skills", "Show skills available to this session"),
                    new SlashDialogOption(ManageId, "Enable or disable skills", "Change an exact skill path"),
                ],
                cancellationToken).ConfigureAwait(false);
            if (action is null)
            {
                return;
            }

            if (string.Equals(action.Id, ListId, StringComparison.Ordinal))
            {
                await ShowInventory(await session.ListSkills(cancellationToken).ConfigureAwait(false), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await Manage(cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException failure)
        {
            await dialog.ShowError($"skills unavailable: {failure.Status.Detail}", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static SlashDialogOption Option(ProtocolSkill skill) => new(
        skill.Path,
        $"[{(skill.Enabled ? 'x' : ' ')}] {DisplayName(skill)}",
        $"{Description(skill)} — {Scope(skill.Scope)} — {skill.Path}");

    private static string Line(ProtocolSkill skill) =>
        $"[{(skill.Enabled ? 'x' : ' ')}] {DisplayName(skill)} — {Description(skill)} — {Scope(skill.Scope)} — {skill.Path}";

    private static IReadOnlyList<string> ErrorLines(ListSkillsResponse listed) => [.. listed.Errors.Select(error =>
        $"Error: {(error.Path.Length == 0 ? "<unknown>" : error.Path)} — {error.Message}")];

    private static string DisplayName(ProtocolSkill skill) => skill.HasDisplayName && skill.DisplayName.Length > 0
        ? string.Equals(skill.DisplayName, skill.Name, StringComparison.Ordinal)
            ? skill.Name
            : $"{skill.DisplayName} ({skill.Name})"
        : skill.Name;

    private static string Description(ProtocolSkill skill) => skill.HasShortDescription && skill.ShortDescription.Length > 0
        ? skill.ShortDescription
        : skill.Description;

    private static string Scope(ListedSkillScope scope) => scope switch
    {
        ListedSkillScope.SkillScopeRepo => "repo",
        ListedSkillScope.SkillScopeUser => "user",
        ListedSkillScope.SkillScopeSystem => "system",
        _ => "unknown",
    };

    private async Task Manage(CancellationToken cancellationToken)
    {
        while (true)
        {
            var listed = await session.ListSkills(cancellationToken).ConfigureAwait(false);
            if (listed.Skills.Count == 0)
            {
                await ShowInventory(listed, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (listed.Errors.Count > 0)
            {
                await dialog.Show(ErrorLines(listed), cancellationToken).ConfigureAwait(false);
            }

            var selected = await dialog.Select(
                "Enable or disable skills",
                [.. listed.Skills.Select(Option)],
                cancellationToken).ConfigureAwait(false);
            if (selected is null)
            {
                return;
            }

            var skill = listed.Skills.Single(item => string.Equals(item.Path, selected.Id, StringComparison.Ordinal));
            await activity.WaitUntilIdle(cancellationToken).ConfigureAwait(false);
            var configured = await session.ConfigureSkill(skill.Path, !skill.Enabled, cancellationToken)
                .ConfigureAwait(false);
            await refreshCompletion(cancellationToken).ConfigureAwait(false);
            await dialog.Show(
                [$"Skill {(configured.Enabled ? "enabled" : "disabled")}: {DisplayName(configured)} — {configured.Path}"],
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ShowInventory(ListSkillsResponse listed, CancellationToken cancellationToken)
    {
        var lines = listed.Skills.Select(Line).ToList();
        if (lines.Count == 0)
        {
            lines.Add("No skills available.");
        }

        lines.AddRange(ErrorLines(listed));
        await dialog.Show(lines, cancellationToken).ConfigureAwait(false);
    }
}
