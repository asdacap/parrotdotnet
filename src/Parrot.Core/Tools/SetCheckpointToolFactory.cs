using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class SetCheckpointToolFactory(CheckpointService checkpoints) : IToolFactory
{
    public ITool Create(IAgentSession session) => new SetCheckpointTool(checkpoints);
}
