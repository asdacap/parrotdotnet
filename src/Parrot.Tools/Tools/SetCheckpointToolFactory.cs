using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class SetCheckpointToolFactory(ICheckpointService checkpoints, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("set_checkpoint");

    public ITool Create(IAgentSession session) => new SetCheckpointTool(checkpoints);
}
