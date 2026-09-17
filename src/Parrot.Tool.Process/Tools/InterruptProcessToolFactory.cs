using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class InterruptProcessToolFactory(IProcessOwner processes, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("interrupt_process");

    public ITool Create(IAgentSession session) => new InterruptProcessTool(processes);
}
