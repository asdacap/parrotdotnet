using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Process;

internal sealed class ShellProcessOwners(
    UserSessionResources resources,
    ProcessRunner runner,
    CancellationToken lifetime) : IActiveWorkSource
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ShellProcessOwner> _owners = new(StringComparer.Ordinal);
    private bool _settling;
    private Task? _settlement;

    public ShellProcessOwner Prepare(string sessionId)
    {
        lock (_gate)
        {
            ValidateRegistration(sessionId);
            return new ShellProcessOwner(sessionId, resources, runner, lifetime);
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

    public IReadOnlyList<ActiveWorkObservation> Active()
    {
        lock (_gate)
        {
            return [.. _owners.Values
                .SelectMany(owner => owner.Active())
                .OrderBy(item => item.Id, StringComparer.Ordinal)];
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
