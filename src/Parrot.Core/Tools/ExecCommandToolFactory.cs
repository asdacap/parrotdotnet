using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class ExecCommandToolFactory(
    ShellProcessOwner processes,
    IReadOnlyList<string> readOnlyCommandPrefixes) : IToolFactory
{
    private readonly ReadOnlyExecCommandClassifier _readOnlyCommandClassifier = new(readOnlyCommandPrefixes);

    public ExecCommandToolFactory(ShellProcessOwner processes)
        : this(processes, [])
    {
    }

    public ITool Create(AgentSession session) =>
        new ExecCommandTool(processes, session, _readOnlyCommandClassifier);
}
