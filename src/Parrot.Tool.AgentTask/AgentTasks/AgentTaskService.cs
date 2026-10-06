using System.Diagnostics;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.AgentTasks;

internal sealed class AgentTaskService(
    IAgentSessionScope ownerScope,
    string ownerAgentSessionId,
    IModelRouter router,
    AgentTaskConfig configuration,
    AgentTaskNotifier notifier,
    IEventBroker eventBroker,
    IEventRepository eventRepository,
    IDiagnosticLog diagnostics,
    CancellationToken lifetime) : IAgentTaskService
{
    private const string NotificationCause = "AgentTask update";
    private readonly List<Entry> _entries = [];
    private readonly HashSet<Task> _executions = [];
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private readonly AgentTaskRunner _runner = new(configuration);
    private readonly Lock _gate = new();
    private ulong _revision;
    private bool _complete;
    private bool _allSucceeded;
    private Task? _settlement;

    public void SetTasks(IReadOnlyList<AgentTask> tasks, AgentTurnSelection selection, HistoryForkBoundary historyBoundary)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(historyBoundary);
        lock (_gate)
        {
            if (_settlement is not null)
            {
                throw new InvalidOperationException("The agent session is shutting down.");
            }

            var incomingNames = tasks.Select(task => task.Name).ToHashSet(StringComparer.Ordinal);
            AgentTaskParser.ValidateGraph(
                [.. _entries.Select(entry => entry.Task).Where(task => !incomingNames.Contains(task.Name)), .. tasks],
                "tasks");
            var before = _entries.ToDictionary(entry => entry.Task.Name, Effective, StringComparer.Ordinal);
            foreach (var task in tasks)
            {
                if (Find(task.Name) is { } entry)
                {
                    Apply(entry, task);
                }
                else
                {
                    var boundary = historyBoundary is HistoryForkBoundary.AfterCompletedHistory
                        ? new HistoryForkBoundary.AfterSafeHistoryPrefix()
                        : historyBoundary;
                    _entries.Add(new Entry(
                        task with { State = task.State == AgentTaskExecutionStatus.Running ? AgentTaskExecutionStatus.Pending : task.State },
                        selection,
                        boundary));
                }
            }

            Reconcile();
            var changed = _entries
                .Where(entry => !before.TryGetValue(entry.Task.Name, out var state) || state != Effective(entry))
                .ToArray();
            if (changed.Length > 0)
            {
                notifier.Enqueue(Render("agent-task.notification-updated", ("items", Items(changed))), NotificationCause, false);
            }

            NotifyCompletion();
            Publish();
        }
    }

    public IReadOnlyList<AgentTask> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries.Select(entry => entry.Task with { State = Effective(entry) })];
        }
    }

    public AgentTaskDetail? CaptureDetail(string name)
    {
        lock (_gate)
        {
            return Find(name) is { } entry
                ? new AgentTaskDetail(entry.Task with { State = Effective(entry) }, entry.Scope?.Session.Name)
                : null;
        }
    }

    public void ApplyVisibilityChanges(IReadOnlyList<AgentTask> previous, IReadOnlyList<AgentTask> incoming)
    {
        lock (_gate)
        {
            if (_settlement is not null)
            {
                return;
            }

            var changed = false;
            foreach (var before in previous)
            {
                if (incoming.FirstOrDefault(task => task.Name == before.Name) is not { } after
                    || !before.HasSameDefinition(after)
                    || before.HasSameVisibility(after)
                    || Find(after.Name) is not { } entry
                    || !entry.Task.HasSameDefinition(after))
                {
                    continue;
                }

                var updated = entry.Task.ApplyVisibilityChanges(before, after);
                entry.Task = updated;
                changed = true;
            }

            if (changed)
            {
                Publish();
            }
        }
    }

    public Task Settle()
    {
        lock (_gate)
        {
            return _settlement ??= SettleCore();
        }
    }

    public ValueTask DisposeAsync() => new(Settle());

    private async Task SettleCore()
    {
        Task[] executions;
        lock (_gate)
        {
            var stopReason = Render("agent-task.stop-shutdown");
            foreach (var entry in _entries)
            {
                if (entry.Run is { } run)
                {
                    run.StopReason = stopReason;
                    entry.Run = null;
                }

                if (Effective(entry) is AgentTaskExecutionStatus.Pending or AgentTaskExecutionStatus.Running or AgentTaskExecutionStatus.Failed)
                {
                    entry.Task = entry.Task with { State = AgentTaskExecutionStatus.Canceled };
                }
            }

            if (_entries.Count > 0)
            {
                Publish();
            }

            executions = [.. _executions];
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(executions).ConfigureAwait(false);
        await notifier.Settle().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private void Apply(Entry entry, AgentTask incoming)
    {
        var stored = entry.Task;
        if (entry.Run is not null && incoming.State == AgentTaskExecutionStatus.Running && stored.HasSameDefinition(incoming))
        {
            entry.Task = stored with { Hidden = incoming.Hidden, Payload = incoming.Payload };
            return;
        }

        var state = incoming.State == AgentTaskExecutionStatus.Running ? AgentTaskExecutionStatus.Pending : incoming.State;
        if (entry.Run is not null)
        {
            var reasonTemplate = state switch
            {
                AgentTaskExecutionStatus.Canceled => "agent-task.stop-canceled",
                AgentTaskExecutionStatus.Succeeded => "agent-task.stop-succeeded",
                AgentTaskExecutionStatus.Failed => "agent-task.stop-failed",
                _ => "agent-task.stop-restarted",
            };
            Stop(entry, reasonTemplate);
        }

        if (stored.State == AgentTaskExecutionStatus.Succeeded && state == AgentTaskExecutionStatus.Pending)
        {
            ResetDependents(stored.Name);
        }

        entry.Task = incoming with { State = state };
    }

    private void ResetDependents(string name)
    {
        var reset = new HashSet<string>(StringComparer.Ordinal) { name };
        var pending = new Queue<string>([name]);
        while (pending.TryDequeue(out var current))
        {
            foreach (var dependent in _entries.Where(entry => entry.Task.Dependencies.Contains(current, StringComparer.Ordinal)))
            {
                if (!reset.Add(dependent.Task.Name))
                {
                    continue;
                }

                pending.Enqueue(dependent.Task.Name);
                if (dependent.Task.State is not (AgentTaskExecutionStatus.Running or AgentTaskExecutionStatus.Succeeded or AgentTaskExecutionStatus.Failed))
                {
                    continue;
                }

                if (dependent.Run is not null)
                {
                    Stop(dependent, "agent-task.stop-dependency-reset");
                }

                dependent.Task = dependent.Task with { State = AgentTaskExecutionStatus.Pending, Result = null, Failure = null };
            }
        }
    }

    private void Reconcile()
    {
        foreach (var entry in _entries.Where(entry => entry.Run is not null && Effective(entry) == AgentTaskExecutionStatus.Canceled))
        {
            Stop(entry, "agent-task.stop-dependency-canceled");
            entry.Task = entry.Task with { State = AgentTaskExecutionStatus.Pending };
        }

        if (_settlement is not null)
        {
            return;
        }

        foreach (var entry in _entries)
        {
            if (entry.Run is null
                && entry.Task.State == AgentTaskExecutionStatus.Pending
                && Effective(entry) == AgentTaskExecutionStatus.Pending
                && entry.Task.Dependencies.All(name => Find(name) is { Task.State: AgentTaskExecutionStatus.Succeeded }))
            {
                Start(entry);
            }
        }
    }

    private void Start(Entry entry)
    {
        var task = entry.Task;
        var siblings = _entries.Where(sibling => !ReferenceEquals(sibling, entry)).Select(sibling => sibling.Task).ToArray();
        var dependencies = task.Dependencies
            .Select(name => Find(name)?.Task ?? throw new InvalidOperationException($"AgentTask dependency '{name}' is not declared."))
            .ToArray();
        var started = Stopwatch.GetTimestamp();
        try
        {
            entry.Scope ??= Spawn(task, entry.Selection, entry.HistoryBoundary);
        }
        catch (Exception failure)
        {
            entry.Task = task with { State = AgentTaskExecutionStatus.Failed, Result = null, Failure = failure.Message };
            WriteDiagnostic(entry, "terminal", started);
            notifier.Enqueue(
                Render(
                    "agent-task.notification-failed",
                    ("name", task.Name),
                    ("description", task.Description),
                    ("failure", failure.Message)),
                NotificationCause,
                true);
            return;
        }

        var run = new Run(_lifetime.Token);
        entry.Task = task with { State = AgentTaskExecutionStatus.Running, Result = null, Failure = null };
        entry.Run = run;
        run.Execution = Execute(entry, run, entry.Task, siblings, dependencies, entry.LastExecution);
        entry.LastExecution = run.Execution;
        _ = _executions.Add(run.Execution);
        WriteDiagnostic(entry, "running", run.Started);
    }

    private async Task Execute(
        Entry entry,
        Run run,
        AgentTask task,
        IReadOnlyList<AgentTask> siblings,
        IReadOnlyList<AgentTask> dependencies,
        Task previous)
    {
        await Task.Yield();
        await previous.WaitAsync(CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        AgentTask? outcome = null;
        try
        {
            Task<AgentTask> execution;
            lock (_gate)
            {
                run.Cancellation.Token.ThrowIfCancellationRequested();
                var scope = entry.Scope ?? throw new InvalidOperationException("A running AgentTask requires an agent scope.");
                execution = _runner.Run(entry.Task, siblings, dependencies, scope, run.Cancellation.Token);
            }

            outcome = await execution.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            outcome = task with { State = AgentTaskExecutionStatus.Failed, Failure = failure.Message };
        }

        string? stopReason;
        lock (_gate)
        {
            var current = ReferenceEquals(entry.Run, run);
            if (current && outcome is not null)
            {
                Complete(entry, outcome, run);
            }
            else if (current)
            {
                entry.Run = null;
                entry.Task = entry.Task with { State = AgentTaskExecutionStatus.Canceled };
                Publish();
            }

            stopReason = current && outcome is not null ? null : run.StopReason ?? Render("agent-task.stop-shutdown");
            _ = _executions.Remove(run.Execution);
        }

        run.Cancellation.Dispose();
        if (stopReason is not null)
        {
            await RecordStop(entry, stopReason).ConfigureAwait(false);
        }
    }

    private void Complete(Entry entry, AgentTask outcome, Run run)
    {
        entry.Run = null;
        entry.Task = entry.Task with { State = outcome.State, Result = outcome.Result, Failure = outcome.Failure };
        WriteDiagnostic(entry, "terminal", run.Started);
        if (outcome.State == AgentTaskExecutionStatus.Succeeded)
        {
            notifier.Enqueue(
                Render(
                    "agent-task.notification-succeeded",
                    ("name", outcome.Name),
                    ("description", outcome.Description),
                    ("result", outcome.Result ?? string.Empty)),
                NotificationCause,
                false);
        }
        else
        {
            notifier.Enqueue(
                Render(
                    "agent-task.notification-failed",
                    ("name", outcome.Name),
                    ("description", outcome.Description),
                    ("failure", outcome.Failure ?? string.Empty)),
                NotificationCause,
                true);
        }

        Reconcile();
        NotifyCompletion();
        Publish();
    }

    private async Task RecordStop(Entry entry, string reason)
    {
        if (entry.Scope is not { } scope)
        {
            return;
        }

        try
        {
            await scope.Session.Record(
                [ConversationPart.TextPart(Render("agent-task.force-stopped", ("reason", reason)))],
                Identifier.MessageId(),
                Delivery.Steer,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            diagnostics.Write(new DiagnosticEvent("task", "stop_record", DiagnosticSeverity.Error)
            {
                AgentSessionId = ownerAgentSessionId,
                CorrelationId = entry.DiagnosticId,
                Outcome = "failed",
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
        }
    }

    private void Stop(Entry entry, string reasonTemplate)
    {
        var run = entry.Run ?? throw new InvalidOperationException("Only a running AgentTask can be stopped.");
        run.StopReason = Render(reasonTemplate);
        entry.Run = null;
        _ = run.Cancellation.CancelAsync();
        WriteDiagnostic(entry, "stopped", run.Started);
    }

    private void NotifyCompletion()
    {
        var complete = _entries.Count > 0
            && _entries.All(entry => Effective(entry) is AgentTaskExecutionStatus.Succeeded or AgentTaskExecutionStatus.Canceled);
        if (complete && !_complete)
        {
            notifier.Enqueue(Render("agent-task.notification-all-complete", ("items", Items(_entries))), NotificationCause, true);
        }

        _complete = complete;
    }

    private string Items(IEnumerable<Entry> entries) =>
        string.Concat(entries.Select(entry => Render(
            "agent-task.notification-item",
            ("name", entry.Task.Name),
            ("state", Effective(entry).ToString().ToLowerInvariant()))));

    private AgentTaskExecutionStatus Effective(Entry entry) =>
        entry.Task.State is AgentTaskExecutionStatus.Canceled or AgentTaskExecutionStatus.Succeeded || !HasCanceledDependency(entry.Task)
            ? entry.Task.State
            : AgentTaskExecutionStatus.Canceled;

    private bool HasCanceledDependency(AgentTask task) =>
        task.Dependencies.Any(name => Find(name) is { } dependency
            && (dependency.Task.State == AgentTaskExecutionStatus.Canceled || HasCanceledDependency(dependency.Task)));

    private Entry? Find(string name) => _entries.Find(entry => string.Equals(entry.Task.Name, name, StringComparison.Ordinal));

    private IAgentSessionScope Spawn(AgentTask task, AgentTurnSelection selection, HistoryForkBoundary historyBoundary) =>
        ownerScope.AgentSpawner.GetOrSpawnScope(task.Name, () =>
        {
            AgentHistorySource source = configuration.ForkHistoryMode == AgentTaskForkHistoryMode.Dependency
                && task.Dependencies.Count > 0
                && Find(task.Dependencies[0])?.Scope is { } dependencyScope
                    ? new AgentHistorySource.Sibling(dependencyScope.Session)
                    : new AgentHistorySource.Parent();
            return new AgentLaunchRequest(
                ownerScope.Session,
                selection,
                "agent-task-payload",
                task.Model is null ? selection.RequestedModel : router.Resolve(task.Model).RequestedSelector,
                task.Name,
                Render("agent-task.child-scope", ("role", "execute"), ("task_name", task.Name)),
                HistoryForkSelection.Parse(configuration.ForkHistoryMode == AgentTaskForkHistoryMode.Empty ? string.Empty : "full"),
                historyBoundary,
                AgentCompletionDeliveryPolicy.RetainedOnly,
                source);
        });

    private void Publish()
    {
        var allSucceeded = _entries.Count > 0
            && _entries.All(entry => Effective(entry) == AgentTaskExecutionStatus.Succeeded);
        if (allSucceeded && !_allSucceeded)
        {
            foreach (var entry in _entries)
            {
                entry.Task = entry.Task with { Hidden = true };
            }
        }

        _allSucceeded = allSucceeded;
        var snapshot = new AgentTaskProgressSnapshot { Revision = checked(++_revision) };
        snapshot.RootNodes.Add(_entries.Select(entry => new AgentTaskProgressNode
        {
            Name = entry.Task.Name,
            Description = entry.Task.Description,
            Hidden = entry.Task.Hidden,
            Status = Effective(entry) switch
            {
                AgentTaskExecutionStatus.Pending => AgentTaskProgressStatus.Pending,
                AgentTaskExecutionStatus.Running => AgentTaskProgressStatus.Running,
                AgentTaskExecutionStatus.Succeeded => AgentTaskProgressStatus.Succeeded,
                AgentTaskExecutionStatus.Failed => AgentTaskProgressStatus.Failed,
                _ => AgentTaskProgressStatus.Canceled,
            },
            AgentSessionId = entry.Scope?.Session.SessionId ?? string.Empty,
        }));
        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = ownerAgentSessionId,
            AgentTaskProgressSnapshot = snapshot,
        };
        _ = eventRepository.Append(published, null, null);
        eventBroker.Publish(published);
    }

    private void WriteDiagnostic(Entry entry, string operation, long started) =>
        diagnostics.Write(new DiagnosticEvent("task", operation, entry.Task.State == AgentTaskExecutionStatus.Failed ? DiagnosticSeverity.Error : DiagnosticSeverity.Information)
        {
            AgentSessionId = ownerAgentSessionId,
            CorrelationId = entry.DiagnosticId,
            Outcome = entry.Task.State.ToString().ToLowerInvariant(),
            DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        });

    private string Render(string id, params (string Name, string Value)[] values) =>
        configuration.PromptTemplates.Render(id, [.. values.Select(value => new PromptTemplateArgument(value.Name, value.Value))]);

    private sealed class Entry(AgentTask task, AgentTurnSelection selection, HistoryForkBoundary historyBoundary)
    {
        internal string DiagnosticId { get; } = $"task-{Guid.CreateVersion7():n}";

        internal AgentTask Task { get; set; } = task;

        internal IAgentSessionScope? Scope { get; set; }

        internal AgentTurnSelection Selection => selection;

        internal HistoryForkBoundary HistoryBoundary => historyBoundary;

        internal Run? Run { get; set; }

        internal System.Threading.Tasks.Task LastExecution { get; set; } = System.Threading.Tasks.Task.CompletedTask;
    }

    private sealed class Run(CancellationToken lifetime)
    {
        internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);

        internal long Started { get; } = Stopwatch.GetTimestamp();

        internal Task Execution { get; set; } = Task.CompletedTask;

        internal string? StopReason { get; set; }
    }
}
