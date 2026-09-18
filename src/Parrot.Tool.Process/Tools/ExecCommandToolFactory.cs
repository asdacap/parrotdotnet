using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class ExecCommandToolFactory(
    IProcessOwner processes,
    ToolWorkspace workspace,
    IReadOnlyList<string> readOnlyCommandPrefixes,
    ToolDefinitionCatalog definitions) : IToolFactory
{
    private readonly ReadOnlyExecCommandClassifier _readOnlyCommandClassifier = new(readOnlyCommandPrefixes);

    public ExecCommandToolFactory(
        IProcessOwner processes,
        ToolWorkspace workspace,
        ToolDefinitionCatalog definitions)
        : this(processes, workspace, [], definitions)
    {
    }

    public IToolDefinition Definition => definitions.Describe("exec_command");

    public ITool Create(IAgentSession session) =>
        new ExecCommandTool(processes, session, workspace, _readOnlyCommandClassifier);
}
