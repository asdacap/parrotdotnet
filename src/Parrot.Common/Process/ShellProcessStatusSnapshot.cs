using Parrot.Statuses;

namespace Parrot.Process;

internal sealed record ShellProcessStatusSnapshot(
    string OwnerSessionId,
    string ProcessId,
    string Name,
    ActiveWorkState State)
{
    public string Id => $"{OwnerSessionId}/{Name}";
}
