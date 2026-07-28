namespace Parrot.Cli.Enhanced;

internal interface ITerminal
{
    TextWriter Output { get; }

    TextWriter Error { get; }

    bool Color { get; }

    int GetColumns();

    ValueTask<int> Read(byte[] buffer, CancellationToken cancellationToken);
}
