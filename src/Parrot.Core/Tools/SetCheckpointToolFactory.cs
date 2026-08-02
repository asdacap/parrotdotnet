using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class SetCheckpointToolFactory : IToolFactory
{
    public ITool Create(AgentSession session) => new SetCheckpointTool(session);
}
