using Parrot.Agent;
using Parrot.Statuses;

namespace Parrot.Tools;

internal sealed class StatusToolFactory(IRuntimeStatus status, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("status");

    public ITool Create(IAgentSession session) =>
        new StatusTool(status, session);
}
