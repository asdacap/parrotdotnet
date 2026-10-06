using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class WriteStdinToolFactory(IProcessOwner processes, IAgentResolver resolver, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("write_stdin");

    public ITool Create(IAgentSession session) => new WriteStdinTool(processes, resolver);
}
