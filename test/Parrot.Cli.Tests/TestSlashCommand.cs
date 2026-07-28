using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class TestSlashCommand : ISlashCommand
{
    public string Name => "/test";

    public string Summary => "Test command";

    public int Runs { get; private set; }

    public Task Run(CancellationToken cancellationToken)
    {
        Runs++;
        return Task.CompletedTask;
    }
}
