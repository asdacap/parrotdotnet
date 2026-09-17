using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class ExecCommandToolFactory(
    IProcessOwner processes,
    IReadOnlyList<string> readOnlyCommandPrefixes,
    ToolDefinitionCatalog definitions) : IToolFactory
{
    private readonly ReadOnlyExecCommandClassifier _readOnlyCommandClassifier = new(readOnlyCommandPrefixes);

    public ExecCommandToolFactory(IProcessOwner processes, ToolDefinitionCatalog definitions)
        : this(processes, [], definitions)
    {
    }

    public IToolDefinition Definition => definitions.Describe("exec_command");

    public ITool Create(IAgentSession session) =>
        new ExecCommandTool(processes, session, _readOnlyCommandClassifier);
}
