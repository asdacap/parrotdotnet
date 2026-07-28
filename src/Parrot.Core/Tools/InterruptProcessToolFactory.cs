using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class InterruptProcessToolFactory(ShellProcessOwner processes) : IToolFactory
{
    public ITool? Create(AgentSession session, AgentTurnSelection selection) => new InterruptProcessTool(processes);
}
