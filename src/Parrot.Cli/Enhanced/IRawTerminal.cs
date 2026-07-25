namespace Parrot.Cli.Enhanced;

internal interface IRawTerminal : IDisposable
{
    ValueTask<int> Read(byte[] buffer, CancellationToken cancellationToken);
}
