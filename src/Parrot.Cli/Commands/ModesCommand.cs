using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

internal sealed class ModesCommand(GeneratedParrot.ParrotClient client, ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/modes";

    public string Summary => "List the available modes";

    public async Task Run(string arguments, CancellationToken cancellationToken)
    {
        var listed = await client.ListModesAsync(new ListModesRequest(), cancellationToken: cancellationToken);
        await dialog.Show([.. listed.Modes.Select(mode => mode.Id)], cancellationToken).ConfigureAwait(false);
    }
}
