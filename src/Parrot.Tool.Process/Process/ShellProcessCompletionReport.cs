using Parrot.Protocol;

namespace Parrot.Process;

internal sealed class ShellProcessCompletionReport : IShellProcessReport
{
    public bool BlocksTurn => true;

    public ShellProcessActivityKind ActivityKind => ShellProcessActivityKind.Execution;

    public Task Observe(ActiveShellProcessState state, IProcessExecution execution, CancellationToken lifetime) =>
        Task.CompletedTask;

    public string Complete(ActiveShellProcessState state, string output) =>
        $"Shell process '{state.Name}' completed.\n{output}";
}
