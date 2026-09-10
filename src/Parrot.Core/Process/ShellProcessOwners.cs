using Parrot.Agent;
using Parrot.Diagnostics;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Process;

internal sealed class ShellProcessOwners(
    UserSessionResources resources,
    ProcessRunner runner,
    IDiagnosticLog diagnostics,
    CancellationToken lifetime) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ShellProcessOwner> _owners = new(StringComparer.Ordinal);
    private readonly ShellProcessInventory _inventory = new();
    private bool _settling;
    private Task? _settlement;

    public ShellProcessOwner Prepare(string sessionId, AgentPathEnvironment pathEnvironment)
    {
        lock (_gate)
        {
            ValidateRegistration(sessionId);
            return new ShellProcessOwner(
                sessionId,
                resources,
                resources.AgentScratch(sessionId),
                pathEnvironment,
                runner,
                _inventory,
                diagnostics,
                lifetime);
        }
    }

    public void Register(ShellProcessOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        lock (_gate)
        {
            ValidateRegistration(owner.SessionId);
            _owners.Add(owner.SessionId, owner);
        }
    }

    public ShellProcessInventorySubscription SubscribeInventory() => _inventory.Subscribe();

    public IReadOnlyList<ActiveWorkObservation> Active()
    {
        lock (_gate)
        {
            return [.. _owners.Values
                .SelectMany(owner => owner.Active())
                .OrderBy(item => item.Id, StringComparer.Ordinal)];
        }
    }

    public IReadOnlyList<ShellProcessStatusSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return Array.AsReadOnly(_owners.Values
                .SelectMany(owner => owner.Snapshot())
                .OrderBy(item => item.OwnerSessionId, StringComparer.Ordinal)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.ProcessId, StringComparer.Ordinal)
                .ToArray());
        }
    }

    public Task Settle()
    {
        lock (_gate)
        {
            if (_settlement is null)
            {
                _settling = true;
                _settlement = Task.WhenAll(_owners.Values.Select(owner => owner.Settle()));
            }

            return _settlement;
        }
    }

    public void Dispose() => _inventory.Dispose();

    private void ValidateRegistration(string sessionId)
    {
        if (_settling || lifetime.IsCancellationRequested)
        {
            throw new InvalidOperationException("The user session is shutting down.");
        }

        if (_owners.ContainsKey(sessionId))
        {
            throw new InvalidOperationException($"Shell process owner '{sessionId}' is already registered.");
        }
    }
}
