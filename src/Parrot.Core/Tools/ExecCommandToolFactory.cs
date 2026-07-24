using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class ExecCommandToolFactory(
    string workingDirectory,
    string blobDirectory,
    ProcessRunner processes) : IToolFactory
{
    public ITool Create(AgentSession session) => new ExecCommandTool(workingDirectory, blobDirectory, processes);
}
