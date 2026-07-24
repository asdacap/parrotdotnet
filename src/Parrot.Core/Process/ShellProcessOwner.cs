using Parrot.Agent;

namespace Parrot.Process;

internal sealed class ShellProcessOwner(
    string workingDirectory,
    string blobDirectory,
    ProcessRunner runner,
    CancellationToken lifetime)
{
    private readonly Dictionary<string, ManagedShellProcess> _processes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _generated;
    private bool _settling;

    public ManagedShellProcess Start(string? requestedName, string command, AgentSession agent)
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

            var result = runner.Run(command, workingDirectory, blobDirectory, lifetime);
            var process = new ManagedShellProcess(name, agent, result, lifetime);
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
