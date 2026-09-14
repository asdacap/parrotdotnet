using Parrot.Agent;
using Parrot.Statuses;

namespace Parrot.Process;

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
