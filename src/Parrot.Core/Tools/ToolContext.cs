using Parrot.Process;

namespace Parrot.Tools;

internal sealed class ToolContext(
    string workingDirectory, ProcessRunner processes, ISubagentHost subagents, int depth) : IToolContext
{
    public string WorkingDirectory { get; } = workingDirectory;

    public ProcessRunner Processes { get; } = processes;

    public ISubagentHost Subagents { get; } = subagents;

    public int Depth { get; } = depth;
}
