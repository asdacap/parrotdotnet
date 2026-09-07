namespace Parrot.Cli.Enhanced;

/// <summary>Owns raw terminal mode, restoring the original terminal attributes on disposal.</summary>
internal interface IRawTerminal : IDisposable
{
    /// <summary>Reads raw bytes into the buffer; zero bytes is an idle poll tick, not end of input.</summary>
    ValueTask<int> Read(byte[] buffer, CancellationToken cancellationToken);
}
