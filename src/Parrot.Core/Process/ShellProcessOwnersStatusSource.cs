namespace Parrot.Process;

internal sealed class ShellProcessOwnersStatusSource(ShellProcessOwners owners) : IProcessStatusSource
{
    public IReadOnlyList<ShellProcessStatusSnapshot> Snapshot() => owners.Snapshot();
}
