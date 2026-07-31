namespace Parrot.Process;

internal sealed record ShellWaitResult(
    string Name,
    bool Running,
    string Output,
    ProcessResult? Result,
    YieldedShellProcess? YieldedProcess)
{
    public bool Yielded => YieldedProcess is not null;

    public string Format()
    {
        if (Result is not null)
        {
            return ProcessResultFormatter.Format(Result);
        }

        if (Output.Length == 0)
        {
            return Name;
        }

        return Output.EndsWith('\n')
            ? Output + Name
            : $"{Output}\n{Name}";
    }
}
