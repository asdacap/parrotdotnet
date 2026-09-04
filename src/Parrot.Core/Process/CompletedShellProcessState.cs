namespace Parrot.Process;

internal sealed record CompletedShellProcessState(
    string ProcessId,
    long? ElapsedMilliseconds);
