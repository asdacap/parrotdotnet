namespace Parrot.Cli.Enhanced;

/// <summary>Coordinates decoded keyboard input and the live input surface for its current owner.</summary>
internal interface ILiveInputHost
{
    ValueTask<TerminalKey> ReadKey(CancellationToken cancellationToken);

    /// <summary>Replaces the input surface with the supplied render items.</summary>
    Task ReplaceInput(IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken);

    /// <summary>Discards buffered keys and partial decoding state when input ownership changes.</summary>
    void ResetInput();
}
