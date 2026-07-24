using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class WaitShellToolFactory(ShellProcessOwner processes) : IToolFactory
{
    public ITool Create(AgentSession session) => new WaitShellTool(processes);
}
