namespace Parrot.Cli.Enhanced;

internal interface ITerminal
{
    TextReader Input { get; }

    TextWriter Output { get; }

    TextWriter Error { get; }

    bool Color { get; }

    int GetColumns();

    ValueTask<int> Read(byte[] buffer, CancellationToken cancellationToken);
}
