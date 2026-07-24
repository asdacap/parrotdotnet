namespace Parrot.Cli;

internal interface IRawTerminal : IDisposable
{
    ValueTask<int> Read(byte[] buffer, CancellationToken cancellationToken);
}
