namespace Parrot.Process;

internal sealed record ShellWaitResult(string Name, ProcessResult? Result)
{
    public bool Yielded => Result is null;
}
