namespace Parrot.Cli.Commands;

internal interface ISlashSession
{
    string Id { get; }

    string Model { get; }

    string Mode { get; }

    Task SelectModel(string model, CancellationToken cancellationToken);

    Task SelectMode(string mode, CancellationToken cancellationToken);

    Task StartNew(string model, string mode, CancellationToken cancellationToken);
}
