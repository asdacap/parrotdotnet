using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class WriteStdinToolFactory(ShellProcessOwner processes) : IToolFactory
{
    public ITool Create(AgentSession session) => new WriteStdinTool(processes);
}
