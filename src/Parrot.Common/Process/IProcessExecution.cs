namespace Parrot.Process;

/// <summary>Owns a running shell execution and its output resources until asynchronous disposal.</summary>
internal interface IProcessExecution : IAsyncDisposable
{
    Task<ProcessResult> Result { get; }

    bool IsPseudoTerminal { get; }

    string? StdoutPath { get; }

    string? StderrPath { get; }

    /// <summary>Reads output after the supplied cursor, or pipe output paths for a zero cursor.</summary>
    (long Cursor, string Text) ReadTranscript(long offset);

    Task WriteStdin(string input, CancellationToken cancellationToken);

    Task Cancel();

    void SendSignal(ProcessSignal signal, CancellationToken cancellationToken);

    /// <summary>Reads the completed result after the supplied cursor; throws before completion.</summary>
    (long Cursor, ProcessResult Result) ReadResult(long offset);
}
