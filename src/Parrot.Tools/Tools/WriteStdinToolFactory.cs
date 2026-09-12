using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class WriteStdinToolFactory(IProcessOwner processes) : IToolFactory
{
    public ITool Create(IAgentSession session) => new WriteStdinTool(processes);
}
