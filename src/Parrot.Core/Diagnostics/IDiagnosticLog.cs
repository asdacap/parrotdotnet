namespace Parrot.Diagnostics;

/// <summary>Owns one operational log sink; disposal closes it after its producers have stopped.</summary>
internal interface IDiagnosticLog : IDisposable
{
    /// <summary>Writes and flushes safe metadata. Sink I/O failures disable logging without failing the operation.</summary>
    void Write(DiagnosticEvent entry);
}
