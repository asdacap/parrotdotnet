using System.Diagnostics;
using Parrot.Agent;
using Parrot.Diagnostics;
using Parrot.Security;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Process;

internal sealed class ShellProcessOwner(
    AgentIdentity identity,
    UserSessionResources resources,
    IAgentPathEnvironment pathEnvironment,
    ProcessRunner runner,
    IDiagnosticLog diagnostics,
    CancellationToken lifetime) : IProcessOwner
{
    private readonly ShellProcessInventory _inventory = new(identity);
    private readonly AgentScratchDirectory _scratch = resources.AgentScratch(identity.SessionId);
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private readonly Dictionary<string, IManagedShellProcess> _processes = new(StringComparer.Ordinal);
    private readonly List<IManagedShellProcess> _ownedProcesses = [];
    private readonly Lock _gate = new();
    private int _generated;
    private Task? _settlement;
    private Task? _disposal;

    public string SessionId => identity.SessionId;

    public IShellProcessInventorySubscription SubscribeInventory() => _inventory.Subscribe();

    public ShellProcessInventorySnapshot CaptureInventory() => _inventory.Capture();

    public IManagedShellProcess StartUnattributed(
        string? requestedName,
        string command,
        ProcessEnvironmentOverrides environment,
        IAgentSession agent,
        SecurityProfile securityProfile,
        ShellProcessTerminalMode terminalMode) =>
        Start(
            requestedName,
            command,
            string.Empty,
            environment,
            agent,
            securityProfile,
            terminalMode);

    public IManagedShellProcess StartPipe(
        string? requestedName,
        string command,
        string originToolCallId,
        ProcessEnvironmentOverrides environment,
        IAgentSession agent,
        SecurityProfile securityProfile) =>
        Start(
            requestedName,
            command,
            originToolCallId,
            environment,
            agent,
            securityProfile,
            ShellProcessTerminalMode.Pipe);

    public IManagedShellProcess Start(
        string? requestedName,
        string command,
        string originToolCallId,
        ProcessEnvironmentOverrides environment,
        IAgentSession agent,
        SecurityProfile securityProfile,
        ShellProcessTerminalMode terminalMode)
    {
        lock (_gate)
        {
            if (_settlement is not null || _lifetime.IsCancellationRequested)
            {
                throw new InvalidOperationException("The user session is shutting down.");
            }

            var name = requestedName ?? GenerateName();

            if (_processes.TryGetValue(name, out var existing) && !existing.Retired)
            {
                throw new InvalidOperationException($"Shell process name '{name}' is already reserved.");
            }

            var processId = $"shell-process-{Guid.CreateVersion7():n}";
            var started = Stopwatch.GetTimestamp();
            diagnostics.Write(new DiagnosticEvent("shell", "start", DiagnosticSeverity.Information)
            {
                AgentSessionId = identity.SessionId,
                CorrelationId = processId,
            });
            IProcessExecution execution;
            try
            {
                execution = runner.Start(
                    command,
                    pathEnvironment.Merge(environment),
                    resources,
                    _scratch,
                    securityProfile,
                    terminalMode,
                    _lifetime.Token);
            }
            catch (Exception failure)
            {
                diagnostics.Write(new DiagnosticEvent("shell", "completed", failure is OperationCanceledException ? DiagnosticSeverity.Information : DiagnosticSeverity.Error)
                {
                    AgentSessionId = identity.SessionId,
                    CorrelationId = processId,
                    Outcome = failure is OperationCanceledException ? "cancelled" : "failed",
                    ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
                    DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                });
                throw;
            }

            var state = new ActiveShellProcessState(
                processId,
                name,
                command,
                originToolCallId,
                agent.SessionId,
                agent.Name,
                agent.ParentSessionId,
                agent.ParentSessionName,
                agent.Depth,
                Stopwatch.GetTimestamp());

            var process = new ManagedShellProcess(state, agent, execution, _inventory, diagnostics, _lifetime.Token);
            _processes[name] = process;
            _ownedProcesses.Add(process);
            return process;
        }
    }

    public IManagedShellProcess Claim(string name)
    {
        lock (_gate)
        {
            if (!_processes.TryGetValue(name, out var process))
            {
                throw new InvalidOperationException($"Unknown shell process '{name}'.");
            }

            process.Claim();
            return process;
        }
    }

    public async Task<ShellWaitResult> WriteStdin(
        string name,
        string input,
        TimeSpan yieldAfter,
        CancellationToken cancellationToken)
    {
        var process = Claim(name);
        return await process.WriteStdin(input, yieldAfter, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<ActiveWorkObservation> Active()
    {
        lock (_gate)
        {
            return [.. _processes.Values
                .Where(process => !process.Retired)
                .Select(process => new ActiveWorkObservation(
                    $"{identity.SessionId}/{process.Name}",
                    process.Name,
                    ActiveWorkKind.Shell,
                    ActiveWorkState.Running))
                .OrderBy(item => item.Id, StringComparer.Ordinal)];
        }
    }

    public IReadOnlyList<ShellProcessStatusSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return Array.AsReadOnly(_processes.Values
                .Where(process => !process.Retired)
                .Select(process => new ShellProcessStatusSnapshot(
                    identity.SessionId,
                    process.State.ProcessId,
                    process.Name,
                    ActiveWorkState.Running))
                .OrderBy(item => item.ProcessId, StringComparer.Ordinal)
                .ToArray());
        }
    }

    public Task Settle()
    {
        lock (_gate)
        {
            return _settlement ??= SettleProcesses([.. _ownedProcesses]);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new ValueTask(_disposal ??= DisposeResources(Settle()));
        }
    }

    private async Task SettleProcesses(IManagedShellProcess[] processes)
    {
        await Task.Yield();
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(processes.Select(process => process.Settle())).ConfigureAwait(false);
        }
    }

    private async Task DisposeResources(Task settlement)
    {
        await Task.Yield();
        try
        {
            await settlement.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _inventory.Dispose();
            }
            finally
            {
                _lifetime.Dispose();
            }
        }
    }

    private string GenerateName()
    {
        while (true)
        {
            var candidate = $"shell-{++_generated}";

            if (!_processes.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }
}
