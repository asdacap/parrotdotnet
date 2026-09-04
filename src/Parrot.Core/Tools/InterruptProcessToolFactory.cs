using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class InterruptProcessToolFactory(ShellProcessOwner processes) : IToolFactory
{
    public ITool Create(IAgentSession session) => new InterruptProcessTool(processes);
}
