using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class ReadFileToolFactory(string workingDirectory) : IToolFactory
{
    public ITool Create(AgentSession session) => new ReadFileTool(workingDirectory);
}
