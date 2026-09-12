namespace Parrot.Process;

/// <summary>Provides process-status snapshots without transferring ownership of the observed executions.</summary>
internal interface IProcessStatusSource
{
    IReadOnlyList<ShellProcessStatusSnapshot> Snapshot();
}
