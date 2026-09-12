using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class ExecCommandToolFactory(
    IProcessOwner processes,
    IReadOnlyList<string> readOnlyCommandPrefixes) : IToolFactory
{
    private readonly ReadOnlyExecCommandClassifier _readOnlyCommandClassifier = new(readOnlyCommandPrefixes);

    public ExecCommandToolFactory(IProcessOwner processes)
        : this(processes, [])
    {
    }

    public ITool Create(IAgentSession session) =>
        new ExecCommandTool(processes, session, _readOnlyCommandClassifier);
}
