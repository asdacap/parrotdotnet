using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

internal sealed class EffortCommand(
    GeneratedParrot.ParrotClient client,
    ISlashSession session,
    ISlashActivity activity,
    ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/effort";

    public string Summary => "Switch the model effort for this session";

    public async Task Run(string arguments, CancellationToken cancellationToken)
    {
        var (selector, listed) = await dialog.Load(
            "Loading models…",
            async token =>
            {
                var resolved = await ModelAliasSelection.Resolve(client, session.Model, token).ConfigureAwait(false);
                var models = await client.ListModelsAsync(new ListModelsRequest(), cancellationToken: token);
                return (resolved, models);
            },
            cancellationToken).ConfigureAwait(false);
        var current = ModelSelection.Resolve(listed.Models, selector);
        if (current is null)
        {
            await dialog.ShowError($"unknown selected model: {session.Model}", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (current.Model.Variants.Count == 0)
        {
            await dialog.ShowError(
                $"{current.BaseSelector} exposes no model efforts", cancellationToken).ConfigureAwait(false);
            return;
        }

        var selected = await dialog.Select(
            "Select model effort",
            [.. current.Model.Variants.Select(variant =>
                new SlashDialogOption(variant.Name, variant.Name, variant.ReasoningEffort))],
            cancellationToken).ConfigureAwait(false);
        if (selected is null)
        {
            return;
        }

        var replacement = current.WithVariant(selected.Id);
        if (replacement is null)
        {
            await dialog.ShowError($"unknown model effort {selected.Id}", cancellationToken).ConfigureAwait(false);
            return;
        }

        await activity.WaitUntilIdle(cancellationToken).ConfigureAwait(false);
        await session.SelectModel(replacement.Selector, cancellationToken).ConfigureAwait(false);
        await dialog.Show([$"Model effort selected: {replacement.Variant?.Name}"], cancellationToken)
            .ConfigureAwait(false);
    }
}
