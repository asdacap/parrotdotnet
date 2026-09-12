using Parrot.Diagnostics;

namespace Parrot.Llm;

internal sealed class ProviderSessions(IDiagnosticLog diagnostics, string agentSessionId, LastRequestDumper? lastRequestDumper)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ILLMProvider, ILLMProviderSession> _sessions =
        new(ReferenceEqualityComparer.Instance);

    private bool _disposing;
    private bool _turnOpen;
    private Task? _closeTask;

    public void BeginTurn()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            _turnOpen = true;
            foreach (var session in _sessions.Values)
            {
                session.BeginTurn();
            }
        }
    }

    public ILLMProviderSession Get(ILLMProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            if (_sessions.TryGetValue(provider, out var session))
            {
                return session;
            }

            session = provider.OpenSession()
                ?? throw new InvalidOperationException($"Provider \"{provider.Id}\" returned no session.");
            session = new DiagnosticProviderSession(session, diagnostics, agentSessionId, provider.Id, lastRequestDumper);
            if (_turnOpen)
            {
                session.BeginTurn();
            }

            _sessions.Add(provider, session);
            return session;
        }
    }

    public ValueTask Close()
    {
        TaskCompletionSource? completion = null;
        ILLMProviderSession[]? sessions = null;
        Task closeTask;
        lock (_gate)
        {
            if (_closeTask is null)
            {
                _disposing = true;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _closeTask = completion.Task;
                sessions = [.. _sessions.Values];
                _sessions.Clear();
            }

            closeTask = _closeTask;
        }

        if (completion is not null && sessions is not null)
        {
            _ = CloseSessions(sessions, completion);
        }

        return new ValueTask(closeTask);
    }

    private static async Task CloseSessions(
        IReadOnlyList<ILLMProviderSession> sessions,
        TaskCompletionSource completion)
    {
        Exception? failure = null;
        foreach (var session in sessions)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure is null)
        {
            completion.SetResult();
        }
        else
        {
            completion.SetException(failure);
        }
    }
}
