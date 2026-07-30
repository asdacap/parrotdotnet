using Parrot.Agent;
using Parrot.Security;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Process;

internal sealed class ShellProcessOwner(
    string sessionId,
    UserSessionResources resources,
    ProcessRunner runner,
    CancellationToken lifetime) : IActiveWorkSource
{
    private readonly Dictionary<string, ManagedShellProcess> _processes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _generated;
    private bool _settling;

    public string SessionId => sessionId;

    public ManagedShellProcess Start(
        string? requestedName,
        string command,
        ProcessEnvironmentOverrides environment,
        AgentSession agent,
        SecurityProfile securityProfile)
    {
        string name;

        lock (_gate)
        {
            if (_settling || lifetime.IsCancellationRequested)
            {
                throw new InvalidOperationException("The user session is shutting down.");
            }

            name = requestedName ?? GenerateName();

            if (_processes.ContainsKey(name))
            {
                throw new InvalidOperationException($"Shell process name '{name}' is already reserved.");
            }

            var execution = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            var result = runner.Run(command, environment, resources, securityProfile, execution.Token);
            var process = new ManagedShellProcess(name, agent, result, execution, lifetime);
            _processes.Add(name, process);
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

    public IReadOnlyList<ActiveWorkObservation> Active()
    {
        lock (_gate)
        {
            return [.. _processes.Values
                .Where(process => !process.Completed)
                .Select(process => new ActiveWorkObservation(
                    $"{sessionId}/{process.Name}",
                    process.Name,
                    ActiveWorkKind.Shell,
                    ActiveWorkState.Running))
                .OrderBy(item => item.Id, StringComparer.Ordinal)];
        }
    }

    public async Task Settle()
    {
        ManagedShellProcess[] processes;

        lock (_gate)
        {
            _settling = true;
            processes = [.. _processes.Values];
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
