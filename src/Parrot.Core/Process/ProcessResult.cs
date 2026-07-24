namespace Parrot.Process;

internal sealed record ProcessResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated);
