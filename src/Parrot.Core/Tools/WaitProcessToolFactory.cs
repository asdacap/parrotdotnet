using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class WaitProcessToolFactory(ShellProcessOwner processes) : IToolFactory
{
    public ITool Create(AgentSession session) => new WaitProcessTool(processes);
}
