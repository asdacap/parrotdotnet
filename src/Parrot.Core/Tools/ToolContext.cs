using Parrot.Process;

namespace Parrot.Tools;

internal sealed class ToolContext(string workingDirectory, ProcessRunner processes) : IToolContext
{
    public string WorkingDirectory { get; } = workingDirectory;

    public ProcessRunner Processes { get; } = processes;
}
