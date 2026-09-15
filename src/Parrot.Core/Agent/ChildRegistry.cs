namespace Parrot.Agent;

internal sealed class ChildRegistry(AgentIdentity owner, Action<IAgentSessionScope> validateChildAdmission) : IChildRegistry, IAsyncDisposable
{
    private readonly Dictionary<string, IAgentSessionScope> _childrenByName = new(StringComparer.Ordinal);
    private bool _accepting = true;
    private Task? _shutdown;

    public Lock Gate { get; } = new();

    public bool IsAccepting
    {
        get
        {
            lock (Gate)
            {
                return _accepting;
            }
        }
    }

    public IAgentSessionScope? DetachDirectChildScope(IAgentSessionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (Gate)
        {
            if (_childrenByName.TryGetValue(scope.Session.Name, out var registered)
                && ReferenceEquals(registered, scope))
            {
                _ = _childrenByName.Remove(scope.Session.Name);
                return scope;
            }

            if (!_accepting && _shutdown is not null)
            {
                return null;
            }

            throw new AgentRegistryException($"child agent scope not found: {scope.Session.SessionId}");
        }
    }

    public ValueTask DisposeChildren() => DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        IAgentSessionScope[]? children = null;
        TaskCompletionSource? completion = null;
        Task shutdown;
        lock (Gate)
        {
            if (_shutdown is null)
            {
                _accepting = false;
                children = [.. _childrenByName.Values];
                _childrenByName.Clear();
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _shutdown = completion.Task;
            }

            shutdown = _shutdown;
        }

        if (children is null || completion is null)
        {
            await shutdown.ConfigureAwait(false);
            return;
        }

        Exception? failure = null;
        for (var index = children.Length - 1; index >= 0; index--)
        {
            try
            {
                await children[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure is null)
        {
            _ = completion.TrySetResult();
            return;
        }

        _ = completion.TrySetException(failure);
        await shutdown.ConfigureAwait(false);
    }

    public bool ContainsDescendantScope(IAgentSessionScope candidate)
    {
        foreach (var child in SnapshotChildScopes())
        {
            if (ReferenceEquals(child, candidate) || child.ChildRegistry.ContainsDescendantScope(candidate))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<IAgentSession> SnapshotDescendants()
    {
        var children = SnapshotChildScopes();
        return [.. children.SelectMany(static child =>
            child.ChildRegistry.SnapshotDescendants().Prepend(child.Session))];
    }

    public IAgentSessionScope ResolveNamedChildScope(string name) =>
        FindNamedChildScope(name) ?? throw new AgentRegistryException($"child agent not found: {name}");

    public IAgentSessionScope? FindNamedChildScope(string name)
    {
        lock (Gate)
        {
            return _accepting && _childrenByName.TryGetValue(name, out var child)
                ? child
                : null;
        }
    }

    public bool TryAdd(IAgentSessionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        lock (Gate)
        {
            if (!_accepting)
            {
                return false;
            }

            if (!string.Equals(scope.Session.ParentSessionId, owner.SessionId, StringComparison.Ordinal))
            {
                throw new AgentRegistryException($"parent agent scope not found: {owner.SessionId}");
            }

            validateChildAdmission(scope);
            if (!_childrenByName.TryAdd(scope.Session.Name, scope))
            {
                throw new ChildNameConflictException(
                    $"child agent name is already registered: {scope.Session.Name}");
            }

            try
            {
                scope.PublishSnapshots();
            }
            catch
            {
                _ = _childrenByName.Remove(scope.Session.Name);
                throw;
            }

            return true;
        }
    }

    public IReadOnlyList<IAgentSessionScope> SnapshotChildScopes()
    {
        lock (Gate)
        {
            return _accepting ? [.. _childrenByName.Values] : [];
        }
    }
}
