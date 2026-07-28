using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class InterruptProcessToolFactory(ShellProcessOwner processes) : IToolFactory
{
    public ITool Create(AgentSession session, AgentSelection selection) => new InterruptProcessTool(processes);
}
