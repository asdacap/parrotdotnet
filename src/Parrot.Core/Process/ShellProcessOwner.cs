using System.Diagnostics;
using Parrot.Agent;
using Parrot.Security;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Process;

internal sealed class ShellProcessOwner(
    string sessionId,
    UserSessionResources resources,
    AgentScratchDirectory scratch,
    ProcessRunner runner,
    ShellProcessInventory inventory,
    CancellationToken lifetime)
{
    private readonly Dictionary<string, ManagedShellProcess> _processes = new(StringComparer.Ordinal);
    private readonly List<ManagedShellProcess> _ownedProcesses = [];
    private readonly Lock _gate = new();
    private int _generated;
    private bool _settling;

    public string SessionId => sessionId;

    public ManagedShellProcess Start(
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

    public ManagedShellProcess Start(
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

    public ManagedShellProcess Start(
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
            if (_settling || lifetime.IsCancellationRequested)
            {
                throw new InvalidOperationException("The user session is shutting down.");
            }

            var name = requestedName ?? GenerateName();

            if (_processes.TryGetValue(name, out var existing) && !existing.Retired)
            {
                throw new InvalidOperationException($"Shell process name '{name}' is already reserved.");
            }

            var execution = runner.Start(
                command,
                environment,
                resources,
                scratch,
                securityProfile,
                terminalMode,
                lifetime);
            var state = new ActiveShellProcessState(
                $"shell-process-{Guid.CreateVersion7():n}",
                name,
                command,
                originToolCallId,
                agent.SessionId,
                agent.Name,
                agent.ParentSessionId,
                agent.ParentSessionName,
                agent.Depth,
                Stopwatch.GetTimestamp());

            var process = new ManagedShellProcess(state, agent, execution, inventory, lifetime);
            _processes[name] = process;
            _ownedProcesses.Add(process);
            return process;
        }
    }

    public ManagedShellProcess Claim(string name)
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
                    $"{sessionId}/{process.Name}",
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
                    sessionId,
                    process.State.ProcessId,
                    process.Name,
                    ActiveWorkState.Running))
                .OrderBy(item => item.ProcessId, StringComparer.Ordinal)
                .ToArray());
        }
    }

    public async Task Settle()
    {
        ManagedShellProcess[] processes;

        lock (_gate)
        {
            _settling = true;
            processes = [.. _ownedProcesses];
        }

        await Task.WhenAll(processes.Select(process => process.Settle())).ConfigureAwait(false);
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
