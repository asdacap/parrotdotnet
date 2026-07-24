using Parrot.Process;

namespace Parrot.Tools;

// What a tool is handed at execution.
internal interface IToolContext
{
    string WorkingDirectory { get; }

    ProcessRunner Processes { get; }

    ISubagentHost Subagents { get; }

    // How deep this session sits below the root. A subagent runs at Depth + 1,
    // and the host refuses beyond a limit so recursion terminates.
    int Depth { get; }
}
