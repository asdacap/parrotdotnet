namespace Parrot.Cli.Enhanced;

/// <summary>Provides output writers, display capabilities and raw input for the enhanced CLI without transferring resource ownership.</summary>
internal interface ITerminal
{
    TextWriter Output { get; }

    TextWriter Error { get; }

    bool Color { get; }

    int GetColumns();

    /// <summary>Reads raw bytes into the buffer; zero bytes lets the caller flush pending key decoding and poll again.</summary>
    ValueTask<int> Read(byte[] buffer, CancellationToken cancellationToken);
}
