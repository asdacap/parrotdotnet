using Parrot.Agent;
using Parrot.Statuses;

namespace Parrot.Tools;

internal sealed class WaitToolFactory(IRuntimeStatus status, TimeProvider timeProvider, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("wait");

    public ITool Create(IAgentSession session) =>
        new WaitTool(status, session, timeProvider);
}
