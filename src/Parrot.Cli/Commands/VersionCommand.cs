namespace Parrot.Cli.Commands;

internal sealed class VersionCommand(ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/version";

    public string Summary => "Print the build version";

    public Task Run(string arguments, CancellationToken cancellationToken) =>
        dialog.Show([$"{BuildInfo.ProductName} {BuildInfo.Version}"], cancellationToken);
}
