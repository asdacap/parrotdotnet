using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class InterruptProcessToolFactory(IProcessOwner processes) : IToolFactory
{
    public ITool Create(IAgentSession session) => new InterruptProcessTool(processes);
}
