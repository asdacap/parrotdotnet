using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class ExecCommandToolFactory(string workingDirectory, ProcessRunner processes) : IToolFactory
{
    public ITool Create(AgentSession session) => new ExecCommandTool(workingDirectory, processes);
}
