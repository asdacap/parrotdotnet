using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class ExecCommandToolFactory(ShellProcessOwner processes) : IToolFactory
{
    public ITool Create(AgentSession session, AgentSelection selection) =>
        new ExecCommandTool(processes, session, selection.SecurityProfile);
}
