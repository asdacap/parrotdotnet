using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

internal sealed class ModeSelection(GeneratedParrot.ParrotClient client, ISlashDialog dialog)
{
    public async Task<string?> Select(CancellationToken cancellationToken)
    {
        var listed = await client.ListModesAsync(new ListModesRequest(), cancellationToken: cancellationToken);
        var options = listed.Modes
            .Select(mode => new SlashDialogOption(mode.Id, mode.Id, "Session mode"))
            .ToArray();

        if (options.Length == 0)
        {
            await dialog.ShowError("no modes are available", cancellationToken).ConfigureAwait(false);
            return null;
        }

        var selected = await dialog.Select("Select a mode", options, cancellationToken).ConfigureAwait(false);
        return selected?.Id;
    }
}
