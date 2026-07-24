using Parrot.Process;

namespace Parrot.Tools;

// What a tool is handed at execution. An interface so a test can supply a
// working directory and a runner without a real session.
internal interface IToolContext
{
    string WorkingDirectory { get; }

    ProcessRunner Processes { get; }
}
