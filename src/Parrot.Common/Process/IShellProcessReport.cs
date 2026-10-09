using Parrot.Protocol;

namespace Parrot.Process;

/// <summary>Reports a managed shell process to its agent while it runs and when it completes.</summary>
internal interface IShellProcessReport
{
    /// <summary>Gets a value indicating whether the running process keeps the agent's turn from ending.</summary>
    bool BlocksTurn { get; }

    /// <summary>Gets the activity kind presented for the running process.</summary>
    ShellProcessActivityKind ActivityKind { get; }

    /// <summary>Reports the running execution until it completes or the lifetime ends.</summary>
    Task Observe(ActiveShellProcessState state, IProcessExecution execution, CancellationToken lifetime);

    /// <summary>Renders the completion message once observation has finished.</summary>
    string Complete(ActiveShellProcessState state, string output);
}
