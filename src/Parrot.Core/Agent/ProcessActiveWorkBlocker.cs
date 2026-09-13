using Parrot.Process;
using Parrot.Statuses;

namespace Parrot.Agent;

internal sealed class ProcessActiveWorkBlocker(IProcessOwner processes) : IActiveWorkBlocker
{
    public ActiveWorkBlockerResult? Observe()
    {
        var ownedProcesses = processes.Active();
        return ownedProcesses.Count == 0
            ? null
            : new ActiveWorkBlockerResult(
                new ActiveWorkSection("Running processes", ownedProcesses).Format(),
                null);
    }
}
