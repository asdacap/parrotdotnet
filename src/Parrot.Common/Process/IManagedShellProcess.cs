namespace Parrot.Process;

/// <summary>Coordinates exclusive interaction and completion delivery for an owner-managed shell process.</summary>
internal interface IManagedShellProcess
{
    ActiveShellProcessState State { get; }

    string Name { get; }

    bool Completed { get; }

    bool Retired { get; }

    /// <summary>Reserves interaction with this process, throwing if already claimed or delivered.</summary>
    void Claim();

    /// <summary>Waits for completion or yields available output, releasing the interaction claim.</summary>
    Task<ShellWaitResult> Wait(TimeSpan? yieldAfter, CancellationToken cancellationToken);

    /// <summary>Writes input and waits for output, releasing the interaction claim.</summary>
    Task<ShellWaitResult> WriteStdin(string input, TimeSpan yieldAfter, CancellationToken cancellationToken);

    /// <summary>Sends a signal and releases the interaction claim.</summary>
    Task SendSignal(ProcessSignal signal, CancellationToken cancellationToken);

    /// <summary>Waits for completion delivery and disposes the underlying execution.</summary>
    Task Settle();
}
