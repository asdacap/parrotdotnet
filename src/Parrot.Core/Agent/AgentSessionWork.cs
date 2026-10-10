namespace Parrot.Agent;

internal sealed class AgentSessionWork
{
    private readonly Queue<Request> _pending = [];
    private bool _closed;

    // Shared with the drain so enqueueing and waking cannot race its settlement.
    internal Lock Gate { get; } = new();

    internal bool HasPending
    {
        get
        {
            lock (Gate)
            {
                return _pending.Count > 0;
            }
        }
    }

    internal Task Enqueue(
        Func<AgentTurnSelection, CancellationToken, Task> execute,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new Request(execute, cancellationToken);
        lock (Gate)
        {
            if (_closed)
            {
                throw new AgentRegistryException("the user session is shutting down");
            }

            _pending.Enqueue(request);
        }

        return request.Completion.Task.WaitAsync(cancellationToken);
    }

    internal async Task Run(
        Func<CancellationToken, Task<AgentTurnSelection>> prepare,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Request? request;
            lock (Gate)
            {
                _ = _pending.TryDequeue(out request);
            }

            if (request is null)
            {
                return;
            }

            try
            {
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    request.CancellationToken);
                operation.Token.ThrowIfCancellationRequested();
                var selection = await prepare(operation.Token).ConfigureAwait(false);
                await request.Execute(selection, operation.Token).ConfigureAwait(false);
                _ = request.Completion.TrySetResult();
            }
            catch (OperationCanceledException failure)
            {
                _ = request.Completion.TrySetCanceled(failure.CancellationToken);
            }
            catch (Exception failure)
            {
                _ = request.Completion.TrySetException(failure);
            }
        }
    }

    internal void CancelPending(CancellationToken cancellationToken)
    {
        lock (Gate)
        {
            while (_pending.TryDequeue(out var request))
            {
                _ = request.Completion.TrySetCanceled(cancellationToken);
            }
        }
    }

    internal void Close()
    {
        lock (Gate)
        {
            _closed = true;
            CancelPending(new CancellationToken(canceled: true));
        }
    }

    private sealed class Request(
        Func<AgentTurnSelection, CancellationToken, Task> execute,
        CancellationToken cancellationToken)
    {
        internal Func<AgentTurnSelection, CancellationToken, Task> Execute { get; } = execute;

        internal CancellationToken CancellationToken { get; } = cancellationToken;

        internal TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
