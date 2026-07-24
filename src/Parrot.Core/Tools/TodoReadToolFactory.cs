using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class TodoReadToolFactory : IToolFactory
{
    public ITool Create(AgentSession session) => new TodoReadTool(session);
}
