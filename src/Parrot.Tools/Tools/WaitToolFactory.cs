using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class WaitToolFactory(IReadOnlyList<IActiveWorkBlocker> blockers, TimeProvider timeProvider, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("wait");

    public ITool Create(IAgentSession session) =>
        new WaitTool(blockers, session, timeProvider);
}
