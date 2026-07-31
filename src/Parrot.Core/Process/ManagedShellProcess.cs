using Parrot.Agent;
using Parrot.Protocol;

namespace Parrot.Process;

internal sealed class ManagedShellProcess
{
    private readonly AgentSession _agent;
    private readonly Task<ProcessResult> _completion;
    private readonly ShellProcessExecution _execution;
    private readonly ShellProcessInventory _inventory;
    private readonly CancellationToken _lifetime;
    private readonly Lock _gate = new();
    private readonly Task _delivery;
    private readonly YieldedShellProcess _startVisibility;
    private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _retirement = Task.CompletedTask;
    private long _cursor;
    private bool _claimed = true;
    private bool _delivered;

    public ManagedShellProcess(
        ActiveShellProcessState state,
        AgentSession agent,
        ShellProcessExecution execution,
        ShellProcessInventory inventory,
        CancellationToken lifetime)
    {
        State = state;
        _agent = agent;
        _execution = execution;
        _inventory = inventory;
        _lifetime = lifetime;
        _startVisibility = inventory.Publish(state);
        _completion = ObserveCompletion(execution.Result);
        _delivery = DeliverWhenUnclaimed();
    }

    public ActiveShellProcessState State { get; }

    public string Name => State.Name;

    public bool Completed => _completion.IsCompleted;

    public bool Retired
    {
        get
        {
            lock (_gate)
            {
                return _delivered;
            }
        }
    }

    public void Claim()
    {
        lock (_gate)
        {
            if (_delivered)
            {
                throw new InvalidOperationException($"Shell process '{Name}' has already been delivered.");
            }

            if (_claimed)
            {
                throw new InvalidOperationException($"Shell process '{Name}' is already being waited for.");
            }

            _claimed = true;
            _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public Task<ShellWaitResult> Wait(TimeSpan? yieldAfter, CancellationToken cancellationToken) =>
        WaitForOutput(yieldAfter, cancellationToken);

    public async Task<ShellWaitResult> WriteStdin(
        string input,
        TimeSpan yieldAfter,
        CancellationToken cancellationToken)
    {
        try
        {
            await _execution.WriteStdin(input, cancellationToken).ConfigureAwait(false);
            return await WaitForOutput(yieldAfter, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseClaim();
            throw;
        }
        catch
        {
            ReleaseClaim();
            throw;
        }
    }

    public async Task<ProcessResult?> Interrupt(CancellationToken cancellationToken)
    {
        await _execution.Cancel().ConfigureAwait(false);

        try
        {
            _ = await _completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            return CommitCompleted().Result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseClaim();
            throw;
        }
        catch (OperationCanceledException)
        {
            MarkDelivered();
            return null;
        }
        catch
        {
            MarkDelivered();
            throw;
        }
    }

    public async Task Settle()
    {
        try
        {
            await _delivery.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _execution.DisposeAsync().ConfigureAwait(false);
            await _retirement.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void DeleteSpeculativeBlob(CompletedRead? completed)
    {
        if (completed is { StartCursor: > 0, Result.Result: { Spilled: true } result })
        {
            File.Delete(result.BlobPath);
        }
    }

    private async Task<ProcessResult> ObserveCompletion(Task<ProcessResult> result)
    {
        try
        {
            return await result.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _inventory.Remove(State.ProcessId);
        }
    }

    private async Task<ShellWaitResult> WaitForOutput(
        TimeSpan? yieldAfter,
        CancellationToken cancellationToken)
    {
        try
        {
            if (yieldAfter is null)
            {
                _ = await _completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                return CommitCompleted();
            }

            var delay = Task.Delay(yieldAfter.Value, cancellationToken);
            var resultTask = _completion.WaitAsync(cancellationToken);
            var completed = await Task.WhenAny(resultTask, delay).ConfigureAwait(false);

            if (completed == resultTask)
            {
                _ = await resultTask.ConfigureAwait(false);
                return CommitCompleted();
            }

            await delay.ConfigureAwait(false);
            return CommitRunning();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseClaim();
            throw;
        }
        catch
        {
            MarkDelivered();
            throw;
        }
    }

    private ShellWaitResult CommitRunning()
    {
        TaskCompletionSource released;
        string output;

        lock (_gate)
        {
            if (_completion.IsCompleted)
            {
                return CommitCompletedLocked();
            }

            (_cursor, output) = _execution.ReadTranscript(_cursor);
            _claimed = false;
            released = _released;
        }

        _ = released.TrySetResult();
        return new ShellWaitResult(Name, true, output, null, _startVisibility);
    }

    private ShellWaitResult CommitCompleted()
    {
        lock (_gate)
        {
            return CommitCompletedLocked();
        }
    }

    private ShellWaitResult CommitCompletedLocked()
    {
        var (cursor, result) = _execution.ReadResult(_cursor);
        _cursor = cursor;
        RetireLocked();
        _ = _released.TrySetResult();
        return new ShellWaitResult(Name, false, string.Empty, result, null);
    }

    private async Task DeliverWhenUnclaimed()
    {
        string? failureText = null;

        try
        {
            _ = await _completion.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception failure)
        {
            failureText = $"error: {failure.Message}";
        }

        while (true)
        {
            Task released;

            lock (_gate)
            {
                if (_delivered)
                {
                    return;
                }

                if (!_claimed)
                {
                    _claimed = true;
                    _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    break;
                }

                released = _released.Task;
            }

            await released.ConfigureAwait(false);
        }

        CompletedRead? completed = null;

        try
        {
            var output = failureText;

            if (output is null)
            {
                completed = ReadCompleted();
                output = completed.Result.Format();
            }

            var text = $"Shell process '{Name}' completed.\n{output}";
            var messageId = Identifier.MessageId();

            try
            {
                _ = await _agent
                    .Admit(text, messageId, Delivery.Steer, _lifetime)
                    .ConfigureAwait(false);
            }
            catch
            {
                _ = await _agent
                    .Admit(text, messageId, Delivery.Steer, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            CommitDelivered(completed);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            DeleteSpeculativeBlob(completed);
            ReleaseClaim();
        }
        catch
        {
            DeleteSpeculativeBlob(completed);
            ReleaseClaim();
        }
    }

    private CompletedRead ReadCompleted()
    {
        lock (_gate)
        {
            var startCursor = _cursor;
            var (finalCursor, result) = _execution.ReadResult(startCursor);
            return new CompletedRead(
                startCursor,
                finalCursor,
                new ShellWaitResult(Name, false, string.Empty, result, null));
        }
    }

    private void CommitDelivered(CompletedRead? completed)
    {
        TaskCompletionSource released;

        lock (_gate)
        {
            if (completed is not null)
            {
                if (_cursor != completed.StartCursor)
                {
                    throw new InvalidOperationException($"Shell process '{Name}' output was consumed concurrently.");
                }

                _cursor = completed.FinalCursor;
            }

            RetireLocked();
            released = _released;
        }

        _ = released.TrySetResult();
    }

    private void MarkDelivered()
    {
        lock (_gate)
        {
            RetireLocked();
            _ = _released.TrySetResult();
        }
    }

    private void ReleaseClaim()
    {
        TaskCompletionSource? released = null;

        lock (_gate)
        {
            if (_claimed && !_delivered)
            {
                _claimed = false;
                released = _released;
            }
        }

        _ = released?.TrySetResult();
    }

    private void RetireLocked()
    {
        _delivered = true;
        _claimed = false;

        if (_retirement.IsCompletedSuccessfully)
        {
            _retirement = _execution.DisposeAsync().AsTask();
        }
    }

    private sealed record CompletedRead(long StartCursor, long FinalCursor, ShellWaitResult Result);
}
