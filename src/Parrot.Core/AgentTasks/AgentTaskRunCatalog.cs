using System.Runtime.ExceptionServices;
using Parrot.Statuses;

namespace Parrot.AgentTasks;

internal sealed class AgentTaskRunCatalog(CancellationToken lifetime) : IAsyncDisposable
{
    private readonly Dictionary<RunKey, AgentTaskRun> _runs = [];
    private readonly List<AgentTaskRun> _ownedRuns = [];
    private readonly List<Exception> _failures = [];
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private readonly Lock _gate = new();
    private bool _accepting = true;
    private Task? _settlement;

    public IReadOnlyList<ActiveWorkObservation> Active()
    {
        lock (_gate)
        {
            return [.. _runs.Values
                .Select(static run => new ActiveWorkObservation(
                    $"{run.Key.OwnerAgentSessionId}/{run.Key.RunId}",
                    run.DisplayName,
                    ActiveWorkKind.AgentTask,
                    ActiveWorkState.Running))
                .OrderBy(static item => item.Id, StringComparer.Ordinal)];
        }
    }

    public ValueTask DisposeAsync() => new(Settle());

    internal void Start(AgentTaskRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DisplayName);
        ArgumentNullException.ThrowIfNull(request.Artifact);
        cancellationToken.ThrowIfCancellationRequested();
        var key = new RunKey(request.OwnerScope.Session.SessionId, request.RunId);

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_accepting || _lifetime.IsCancellationRequested)
            {
                throw new InvalidOperationException("The user session is shutting down.");
            }

            if (_runs.ContainsKey(key))
            {
                throw new InvalidOperationException($"AgentTask run '{request.RunId}' is already active.");
            }

            var run = new AgentTaskRun(key, request, this, _lifetime.Token);
            run.Initialize(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _runs.Add(key, run);
            _ownedRuns.Add(run);
            run.Start();
        }
    }

    internal IReadOnlyList<AgentTaskRunSnapshot> Snapshot(string ownerAgentSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerAgentSessionId);
        lock (_gate)
        {
            return Array.AsReadOnly(_runs.Values
                .Where(run => string.Equals(
                    run.Key.OwnerAgentSessionId,
                    ownerAgentSessionId,
                    StringComparison.Ordinal))
                .OrderBy(run => run.Key.RunId, StringComparer.Ordinal)
                .Select(run => new AgentTaskRunSnapshot(
                    run.Key.OwnerAgentSessionId,
                    run.Key.RunId,
                    run.DisplayName,
                    run.Progress.CurrentSnapshot()))
                .ToArray());
        }
    }

    internal IReadOnlyList<ActiveWorkObservation> Active(string ownerAgentSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerAgentSessionId);
        lock (_gate)
        {
            return [.. _runs.Values
                .Where(run => string.Equals(
                    run.Key.OwnerAgentSessionId,
                    ownerAgentSessionId,
                    StringComparison.Ordinal))
                .Select(static run => new ActiveWorkObservation(
                    $"{run.Key.OwnerAgentSessionId}/{run.Key.RunId}",
                    run.DisplayName,
                    ActiveWorkKind.AgentTask,
                    ActiveWorkState.Running))
                .OrderBy(static item => item.Id, StringComparer.Ordinal)];
        }
    }

    internal Task Settle()
    {
        lock (_gate)
        {
            if (_settlement is null)
            {
                _accepting = false;
                _settlement = SettleRuns();
            }

            return _settlement;
        }
    }

    private async Task SettleRuns()
    {
        await Task.Yield();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        AgentTaskRun[] runs;
        lock (_gate)
        {
            runs = [.. _ownedRuns];
        }

        Exception? failure = null;
        await Task.WhenAll(runs.Select(async run =>
        {
            try
            {
                await run.Settle().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _ = Interlocked.CompareExchange(ref failure, exception, null);
            }
        })).ConfigureAwait(false);

        _lifetime.Dispose();
        lock (_gate)
        {
            failure ??= _failures.FirstOrDefault();
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void Retire(AgentTaskRun run)
    {
        lock (_gate)
        {
            if (_runs.TryGetValue(run.Key, out var current) && ReferenceEquals(current, run))
            {
                _ = _runs.Remove(run.Key);
                _ = _ownedRuns.Remove(run);
            }
        }
    }

    private void RecordFailure(AgentTaskRun run, Exception failure)
    {
        lock (_gate)
        {
            _failures.Add(new InvalidOperationException(
                $"AgentTask run '{run.Key.RunId}' completion delivery failed.",
                failure));
        }
    }

    private sealed class AgentTaskRun(
        RunKey key,
        AgentTaskRunRequest request,
        AgentTaskRunCatalog catalog,
        CancellationToken lifetime)
    {
        private const int MaximumShutdownDeliveryAttempts = 2;
        private static readonly TimeSpan DeliveryRetryDelay = TimeSpan.FromSeconds(1);
        private readonly string _completionMessageId = Identifier.MessageId();
        private Task _execution = Task.CompletedTask;
        private Exception? _deliveryFailure;

        internal RunKey Key => key;

        internal string DisplayName => request.DisplayName;

        internal AgentTaskProgress Progress => request.Progress;

        internal void Initialize(CancellationToken cancellationToken) =>
            _ = request.Progress.Initialize(request.Artifact.Tasks, cancellationToken);

        internal void Start() => _execution = Execute();

        internal async Task Settle() =>
            await _execution.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        private async Task Execute()
        {
            await Task.Yield();
            var runner = new AgentTaskGraphRunner(
                request.Router,
                request.OwnerScope,
                request.Selection,
                request.Progress,
                request.Configuration,
                request.RootHistoryBoundary);
            AgentTaskRunTerminal terminal;
            try
            {
                var result = await runner.Run(request.Artifact, lifetime).ConfigureAwait(false);
                terminal = new AgentTaskRunTerminal(
                    key.RunId,
                    _completionMessageId,
                    result.Status,
                    result.Serialize(),
                    string.Empty);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                terminal = new AgentTaskRunTerminal(
                    key.RunId,
                    _completionMessageId,
                    AgentTaskExecutionStatus.Canceled,
                    string.Empty,
                    "interrupted");
            }
            catch (Exception failure)
            {
                terminal = new AgentTaskRunTerminal(
                    key.RunId,
                    _completionMessageId,
                    AgentTaskExecutionStatus.Failed,
                    string.Empty,
                    failure.Message);
            }

            try
            {
                if (await DeliverUntilShutdown(terminal).ConfigureAwait(false))
                {
                    return;
                }

                for (var attempt = 0; attempt < MaximumShutdownDeliveryAttempts; attempt++)
                {
                    try
                    {
                        await request.Completion.DeliverDuringShutdown(terminal, CancellationToken.None)
                            .ConfigureAwait(false);
                        return;
                    }
                    catch (Exception failure)
                    {
                        _deliveryFailure = failure;
                    }
                }

                catalog.RecordFailure(
                    this,
                    _deliveryFailure ?? new InvalidOperationException("AgentTask completion delivery did not report a failure."));
            }
            finally
            {
                catalog.Retire(this);
            }
        }

        private async Task<bool> DeliverUntilShutdown(AgentTaskRunTerminal terminal)
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await request.Completion.Deliver(terminal, lifetime).ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    return false;
                }
                catch (Exception failure)
                {
                    _deliveryFailure = failure;
                }

                try
                {
                    await Task.Delay(DeliveryRetryDelay, lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    return false;
                }
            }

            return false;
        }
    }

    private sealed record RunKey(string OwnerAgentSessionId, string RunId);
}
