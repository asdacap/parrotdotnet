namespace Parrot.Process;

internal interface IProcessStatusSource
{
    IReadOnlyList<ShellProcessStatusSnapshot> Snapshot();
}
