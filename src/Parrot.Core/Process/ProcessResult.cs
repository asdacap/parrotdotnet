namespace Parrot.Process;

internal sealed record ProcessResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    string BlobPath)
{
    public bool Spilled => BlobPath.Length > 0;
}
