using Parrot.Agent;
using Parrot.Security;
using Parrot.Statuses;

namespace Parrot.Process;

/// <summary>Owns an agent session's shell processes, including inventory, settlement and resource disposal.</summary>
internal interface IProcessOwner : IAsyncDisposable, IAgentWorkOwner
{
    string SessionId { get; }

    /// <summary>Subscribes to the current inventory and subsequent snapshots until disposed.</summary>
    IShellProcessInventorySubscription SubscribeInventory();

    ShellProcessInventorySnapshot CaptureInventory();

    /// <summary>Starts and claims a process without attributing it to a tool call; this owner retains its lifetime.</summary>
    IManagedShellProcess StartUnattributed(
        string? requestedName,
        string command,
        ProcessEnvironmentOverrides environment,
        IAgentSession agent,
        SecurityProfile securityProfile,
        ShellProcessTerminalMode terminalMode);

    /// <summary>Starts and claims a pipe process; this owner retains its lifetime.</summary>
    IManagedShellProcess StartPipe(
        string? requestedName,
        string command,
        string originToolCallId,
        ProcessEnvironmentOverrides environment,
        IAgentSession agent,
        SecurityProfile securityProfile);

    /// <summary>Starts and claims a process in the requested mode; this owner retains its lifetime.</summary>
    IManagedShellProcess Start(
        string? requestedName,
        string command,
        string originToolCallId,
        ProcessEnvironmentOverrides environment,
        IAgentSession agent,
        SecurityProfile securityProfile,
        ShellProcessTerminalMode terminalMode);

    /// <summary>Claims a named process for exclusive interaction, throwing if unavailable.</summary>
    IManagedShellProcess Claim(string name);

    /// <summary>Claims a named process, writes input and waits for output, releasing the claim afterward.</summary>
    Task<ShellWaitResult> WriteStdin(string name, string input, TimeSpan yieldAfter, CancellationToken cancellationToken);

    /// <summary>Captures undelivered processes as active work observations.</summary>
    IReadOnlyList<ActiveWorkObservation> Active();

    /// <summary>Captures undelivered process status snapshots.</summary>
    IReadOnlyList<ShellProcessStatusSnapshot> Snapshot();
}
