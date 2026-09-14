using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Parrot.Diagnostics;
using Parrot.Statuses;

namespace Parrot.AgentTasks;

internal sealed class AgentTaskRunCatalog(string ownerAgentSessionId, IDiagnosticLog diagnostics, CancellationToken lifetime) : IAgentTaskRunCatalog
{
    private readonly Dictionary<string, AgentTaskRun> _runs = new(StringComparer.Ordinal);
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
                .Select(run => new ActiveWorkObservation(
                    $"{ownerAgentSessionId}/{run.RunId}",
                    run.DisplayName,
                    ActiveWorkState.Running))
                .OrderBy(static item => item.Id, StringComparer.Ordinal)];
        }
    }

    public ValueTask DisposeAsync() => new(Settle());

    public void Start(AgentTaskRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DisplayName);
        ArgumentNullException.ThrowIfNull(request.Artifact);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.OwnerScope.Session.SessionId, ownerAgentSessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AgentTask run owner does not match the requesting agent session.");
        }

        var runId = request.RunId;

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_accepting || _lifetime.IsCancellationRequested)
            {
                throw new InvalidOperationException("The agent session is shutting down.");
            }

            if (_runs.ContainsKey(runId))
            {
                throw new InvalidOperationException($"AgentTask run '{request.RunId}' is already active.");
            }

            var run = new AgentTaskRun(runId, request, this, _lifetime.Token);
            run.Initialize(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _runs.Add(runId, run);
            _ownedRuns.Add(run);
            run.Start();
        }
    }

    public IReadOnlyList<AgentTaskRunSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return Array.AsReadOnly(_runs.Values
                .OrderBy(run => run.RunId, StringComparer.Ordinal)
                .Select(run => new AgentTaskRunSnapshot(
                    ownerAgentSessionId,
                    run.RunId,
                    run.DisplayName,
                    run.Progress.CurrentSnapshot()))
                .ToArray());
        }
    }

    public Task Settle()
    {
        lock (_gate)
        {
            if (_settlement is null)
            {
                _accepting = false;
                if (_ownedRuns.Count == 0)
                {
                    _lifetime.Dispose();
                    _settlement = _failures.Count == 0 ? Task.CompletedTask : Task.FromException(_failures[0]);
                }
                else
                {
                    _settlement = SettleRuns();
                }
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

    private void WriteDiagnostic(string runId, string operation, string outcome, long started, Exception? failure) =>
        diagnostics.Write(new DiagnosticEvent("task_run", operation, outcome == "failed" ? DiagnosticSeverity.Error : DiagnosticSeverity.Information)
        {
            AgentSessionId = ownerAgentSessionId,
            CorrelationId = runId,
            Outcome = outcome,
            DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            ErrorCode = failure is null ? null : DiagnosticEvent.ClassifyFailure(failure),
        });

    private void Retire(AgentTaskRun run)
    {
        lock (_gate)
        {
            if (_runs.TryGetValue(run.RunId, out var current) && ReferenceEquals(current, run))
            {
                _ = _runs.Remove(run.RunId);
                _ = _ownedRuns.Remove(run);
            }
        }
    }

    private void RecordFailure(AgentTaskRun run, Exception failure)
    {
        lock (_gate)
        {
            _failures.Add(new InvalidOperationException(
                $"AgentTask run '{run.RunId}' completion delivery failed.",
                failure));
        }
    }

    private sealed class AgentTaskRun(
        string runId,
        AgentTaskRunRequest request,
        AgentTaskRunCatalog catalog,
        CancellationToken lifetime)
    {
        private const int MaximumShutdownDeliveryAttempts = 2;
        private static readonly TimeSpan DeliveryRetryDelay = TimeSpan.FromSeconds(1);
        private readonly string _completionMessageId = Identifier.MessageId();
        private Task _execution = Task.CompletedTask;
        private Exception? _deliveryFailure;

        internal string RunId => runId;

        internal string DisplayName => request.DisplayName;

        internal IAgentTaskProgress Progress => request.Progress;

        internal void Initialize(CancellationToken cancellationToken) =>
            _ = request.Progress.Initialize(request.Artifact.Tasks, cancellationToken);

        internal void Start() => _execution = Execute();

        internal async Task Settle() =>
            await _execution.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        private void WriteDiagnostic(string operation, string outcome, long started, Exception? failure) =>
            catalog.WriteDiagnostic(runId, operation, outcome, started, failure);

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
            var started = Stopwatch.GetTimestamp();
            WriteDiagnostic("start", "running", started, null);
            AgentTaskRunTerminal terminal;
            Exception? executionFailure = null;
            try
            {
                var result = await runner.Run(request.Artifact, lifetime).ConfigureAwait(false);
                terminal = new AgentTaskRunTerminal(
                    runId,
                    _completionMessageId,
                    result.Status,
                    result.Serialize(),
                    string.Empty);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                terminal = new AgentTaskRunTerminal(
                    runId,
                    _completionMessageId,
                    AgentTaskExecutionStatus.Canceled,
                    string.Empty,
                    "interrupted");
            }
            catch (Exception failure)
            {
                executionFailure = failure;
                terminal = new AgentTaskRunTerminal(
                    runId,
                    _completionMessageId,
                    AgentTaskExecutionStatus.Failed,
                    string.Empty,
                    failure.Message);
            }

            WriteDiagnostic("completed", terminal.Status.ToString().ToLowerInvariant(), started, executionFailure);
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

                WriteDiagnostic("delivery", "failed", started, _deliveryFailure);
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
}
