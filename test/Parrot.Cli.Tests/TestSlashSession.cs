using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class TestSlashSession(string model) : ISlashSession
{
    public string Id { get; private set; } = "session-0";

    public string Model { get; private set; } = model;

    public string Mode { get; private set; } = "build";

    public Task SelectModel(string model, CancellationToken cancellationToken)
    {
        Model = model;
        return Task.CompletedTask;
    }

    public Task SelectMode(string mode, CancellationToken cancellationToken)
    {
        Mode = mode;
        return Task.CompletedTask;
    }

    public Task StartNew(string model, string mode, CancellationToken cancellationToken)
    {
        Id = "session-1";
        Model = model;
        Mode = mode;
        return Task.CompletedTask;
    }
}
