using System.Diagnostics;
using Parrot.Agent;
using Parrot.Diagnostics;

namespace Parrot.Process;

internal sealed class ManagedShellProcess : IManagedShellProcess
{
    private readonly IAgentSession _agent;
    private readonly IDiagnosticLog _diagnostics;
    private readonly Task<ProcessResult> _completion;
    private readonly IProcessExecution _execution;
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
        IAgentSession agent,
        IProcessExecution execution,
        ShellProcessInventory inventory,
        IDiagnosticLog diagnostics,
        CancellationToken lifetime)
    {
        State = state;
        _diagnostics = diagnostics;
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

    public Task SendSignal(ProcessSignal signal, CancellationToken cancellationToken)
    {
        try
        {
            _execution.SendSignal(signal, cancellationToken);
            WriteDiagnostic("signal", "sent", null);
            return Task.CompletedTask;
        }
        catch (Exception failure)
        {
            WriteDiagnostic("signal", failure is OperationCanceledException ? "cancelled" : "failed", failure);
            throw;
        }
        finally
        {
            ReleaseClaim();
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

    private void WriteDiagnostic(string operation, string outcome, Exception? failure) =>
        _diagnostics.Write(new DiagnosticEvent("shell", operation, outcome == "failed" ? DiagnosticSeverity.Error : DiagnosticSeverity.Information)
        {
            AgentSessionId = State.OwnerAgentSessionId,
            CorrelationId = State.ProcessId,
            Outcome = outcome,
            DurationMilliseconds = (long)Stopwatch.GetElapsedTime(State.StartedTimestamp).TotalMilliseconds,
            ErrorCode = failure is null ? null : DiagnosticEvent.ClassifyFailure(failure),
        });

    private async Task<ProcessResult> ObserveCompletion(Task<ProcessResult> result)
    {
        ProcessResult? completed = null;
        try
        {
            completed = await result.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            WriteDiagnostic("completed", completed.ExitCode == 0 ? "succeeded" : "failed", null);
            return completed;
        }
        catch (Exception failure)
        {
            WriteDiagnostic("completed", failure is OperationCanceledException ? "cancelled" : "failed", failure);
            throw;
        }
        finally
        {
            _inventory.Complete(State.ProcessId, completed?.ElapsedMilliseconds);
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

        WriteDiagnostic("yield", "running", null);
        _ = released.TrySetResult();
        return new ShellWaitResult(
            Name,
            true,
            output,
            null,
            _startVisibility with
            {
                StdoutPath = _execution.StdoutPath,
                StderrPath = _execution.StderrPath,
            });
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

            try
            {
                await released.WaitAsync(_lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
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
                await _agent
                    .ReceiveProcessCompletion(Name, text, messageId, _lifetime)
                    .ConfigureAwait(false);
            }
            catch
            {
                await _agent
                    .ReceiveProcessCompletion(Name, text, messageId, CancellationToken.None)
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
