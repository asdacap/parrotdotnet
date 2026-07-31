using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class TodoWriteToolFactory : IToolFactory
{
    public ITool Create(AgentSession session) => new TodoWriteTool(session);
}
