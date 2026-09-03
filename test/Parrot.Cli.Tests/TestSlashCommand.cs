using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class TestSlashCommand : ISlashCommand
{
    public string Name => "/test";

    public string Summary => "Test command";

    public int Runs { get; private set; }

    public List<string> Arguments { get; } = [];

    public Task Run(string arguments, CancellationToken cancellationToken)
    {
        Runs++;
        Arguments.Add(arguments);
        return Task.CompletedTask;
    }
}
