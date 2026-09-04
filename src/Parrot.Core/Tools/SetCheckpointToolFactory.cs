using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class SetCheckpointToolFactory : IToolFactory
{
    public ITool Create(IAgentSession session) => new SetCheckpointTool(session);
}
