using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class TodoReadToolFactory : IToolFactory
{
    public ITool Create(AgentSession session, AgentSelection selection) => new TodoReadTool(session);
}
