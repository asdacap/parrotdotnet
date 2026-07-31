using Parrot.Agent;
using Parrot.Store;

namespace Parrot.Queues;

internal sealed class AgentQueueCatalog : IDisposable
{
    private readonly Dictionary<string, AgentQueues> _agents = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly UserSessionResources _resources;
    private bool _disposed;

    public AgentQueueCatalog(UserSessionResources resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        _resources = resources;
        RemoveStaleAgentQueues(resources.AgentQueueRootDirectory);
    }

    public AgentQueues Register(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_agents.ContainsKey(identity.SessionId))
            {
                throw new InvalidOperationException($"Queue owner '{identity.SessionId}' is already registered.");
            }

            AgentQueues? parent = null;
            if (identity.ParentSessionId.Length > 0
                && !_agents.TryGetValue(identity.ParentSessionId, out parent))
            {
                throw new InvalidOperationException($"Queue parent '{identity.ParentSessionId}' is not registered.");
            }

            var directory = identity.Depth == 0
                ? _resources.QueueDirectory
                : _resources.AgentQueueDirectory(identity.SessionId);
            var queues = new AgentQueues(this, identity.SessionId, parent, directory, identity.Depth > 0);

            try
            {
                if (identity.Depth == 0)
                {
                    queues.Local.AdoptRootListener(identity.SessionId);
                }

                _agents.Add(identity.SessionId, queues);
                return queues;
            }
            catch
            {
                queues.Dispose();
                throw;
            }
        }
    }

    public QueueInfo Create(AgentQueues owner, string name, string description)
    {
        lock (_gate)
        {
            RequireRegistered(owner);
            EnsureMissing(owner.Parent, name);

            foreach (var child in _agents.Values.Where(candidate => ReferenceEquals(candidate.Parent, owner)))
            {
                EnsureMissing(child, name);
            }

            return owner.Local.Create(name, description);
        }
    }

    public IReadOnlyList<QueueInfo> List(string sessionId)
    {
        lock (_gate)
        {
            var owner = _agents.GetValueOrDefault(sessionId)
                ?? throw new InvalidOperationException($"Queue owner '{sessionId}' is not registered.");
            return owner.List();
        }
    }

    public async Task Notify(QueueStore store, CancellationToken cancellationToken)
    {
        AgentQueues[] candidates;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            candidates = [.. _agents.Values.Where(candidate => candidate.CanAccess(store))];
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await candidate.Deliver(store, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    public void Unregister(AgentQueues owner)
    {
        lock (_gate)
        {
            if (_agents.TryGetValue(owner.SessionId, out var registered) && ReferenceEquals(registered, owner))
            {
                _ = _agents.Remove(owner.SessionId);
            }
        }
    }

    public void Dispose()
    {
        AgentQueues[] agents;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            agents = [.. _agents.Values.Reverse()];
            _agents.Clear();
        }

        foreach (var agent in agents)
        {
            agent.Dispose();
        }
    }

    private static void RemoveStaleAgentQueues(string directory)
    {
        if (System.IO.Directory.Exists(directory))
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    private static void EnsureMissing(AgentQueues? owner, string name)
    {
        if (owner is null)
        {
            return;
        }

        try
        {
            _ = owner.Local.Get(name, owner.SessionId);
        }
        catch (QueueNotFoundException)
        {
            return;
        }

        throw new QueueAlreadyExistsException($"queue: '{name}' already exists");
    }

    private void RequireRegistered(AgentQueues owner)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_agents.TryGetValue(owner.SessionId, out var registered) || !ReferenceEquals(registered, owner))
        {
            throw new InvalidOperationException($"Queue owner '{owner.SessionId}' is not registered.");
        }
    }
}
