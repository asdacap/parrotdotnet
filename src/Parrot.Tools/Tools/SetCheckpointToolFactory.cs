using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class SetCheckpointToolFactory(ICheckpointService checkpoints) : IToolFactory
{
    public ITool Create(IAgentSession session) => new SetCheckpointTool(checkpoints);
}
