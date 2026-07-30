using Parrot.Agent;
using Parrot.Protocol;

namespace Parrot.Process;

internal sealed class ManagedShellProcess
{
    private readonly AgentSession _agent;
    private readonly CancellationTokenSource _execution;
    private readonly CancellationToken _lifetime;
    private readonly Lock _gate = new();
    private readonly Task<ProcessResult> _result;
    private readonly Task _delivery;
    private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _claimed = true;
    private bool _delivered;

    public ManagedShellProcess(
        string name,
        AgentSession agent,
        Task<ProcessResult> result,
        CancellationTokenSource execution,
        CancellationToken lifetime)
    {
        Name = name;
        _agent = agent;
        _result = result;
        _execution = execution;
        _lifetime = lifetime;
        _delivery = DeliverWhenUnclaimed();
    }

    public string Name { get; }

    public bool Completed => _result.IsCompleted;

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

    public async Task<ShellWaitResult> Wait(TimeSpan? yieldAfter, CancellationToken cancellationToken)
    {
        try
        {
            if (yieldAfter is null)
            {
                var result = await _result.WaitAsync(cancellationToken).ConfigureAwait(false);
                MarkWaitDelivered();
                return new ShellWaitResult(Name, result);
            }

            var delay = Task.Delay(yieldAfter.Value, cancellationToken);
            var resultTask = _result.WaitAsync(cancellationToken);
            var completed = await Task.WhenAny(resultTask, delay).ConfigureAwait(false);

            if (completed == resultTask)
            {
                var result = await resultTask.ConfigureAwait(false);
                MarkWaitDelivered();
                return new ShellWaitResult(Name, result);
            }

            await delay.ConfigureAwait(false);
            ReleaseClaim();
            return new ShellWaitResult(Name, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseClaim();
            throw;
        }
        catch
        {
            MarkWaitDelivered();
            throw;
        }
    }

    public async Task<ProcessResult?> Interrupt(CancellationToken cancellationToken)
    {
        await _execution.CancelAsync().ConfigureAwait(false);

        try
        {
            var result = await _result.WaitAsync(cancellationToken).ConfigureAwait(false);
            MarkWaitDelivered();
            return result;
        }
        catch (OperationCanceledException) when (_execution.IsCancellationRequested)
        {
            MarkWaitDelivered();
            return null;
        }
        catch
        {
            MarkWaitDelivered();
            throw;
        }
    }

    public async Task Settle()
    {
        Task delivery;

        lock (_gate)
        {
            delivery = _delivery;
        }

        try
        {
            await delivery.ConfigureAwait(false);
        }
        finally
        {
            _execution.Dispose();
        }
    }

    private async Task DeliverWhenUnclaimed()
    {
        string output;

        try
        {
            output = ProcessResultFormatter.Format(
                await _result.WaitAsync(CancellationToken.None).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception failure)
        {
            output = $"error: {failure.Message}";
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
                    _delivered = true;
                    break;
                }

                released = _released.Task;
            }

            await released.ConfigureAwait(false);
        }

        var text = $"Shell process '{Name}' completed.\n{output}";

        try
        {
            _ = await _agent
                .Admit(text, Identifier.MessageId(), Delivery.Steer, _lifetime)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void MarkWaitDelivered()
    {
        TaskCompletionSource released;

        lock (_gate)
        {
            _delivered = true;
            _claimed = false;
            released = _released;
        }

        _ = released.TrySetResult();
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
}
