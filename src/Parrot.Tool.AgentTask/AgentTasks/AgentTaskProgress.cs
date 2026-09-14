using System.Diagnostics;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.AgentTasks;

internal sealed class AgentTaskProgress(
    IEventBroker eventBroker,
    IEventRepository eventRepository,
    string ownerAgentSessionId,
    string originToolCallId,
    IDiagnosticLog diagnostics) : IAgentTaskProgress
{
    private readonly Lock _gate = new();
    private readonly Dictionary<AgentTaskNodeHandle, ProgressNode> _nodes = [];
    private readonly List<ProgressNode> _roots = [];
    private ulong _revision;
    private bool _initialized;

    public IReadOnlyList<AgentTaskNodeHandle> Initialize(
        IReadOnlyList<AgentTask> tasks,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_initialized)
            {
                throw new InvalidOperationException("AgentTask progress is already initialized.");
            }

            _roots.AddRange(tasks.Select(BuildNode));
            _initialized = true;
            PublishSnapshot();
            return Handles(_roots);
        }
    }

    public AgentTaskProgressSnapshot CurrentSnapshot()
    {
        lock (_gate)
        {
            return BuildSnapshot(_revision);
        }
    }

    public IReadOnlyList<AgentTaskNodeHandle> EnsureInitialized(
        IReadOnlyList<AgentTask> tasks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_initialized)
            {
                _roots.AddRange(tasks.Select(BuildNode));
                _initialized = true;
                PublishSnapshot();
            }

            return Handles(_roots);
        }
    }

    public IReadOnlyList<AgentTaskNodeHandle> GetChildren(AgentTaskNodeHandle handle)
    {
        lock (_gate)
        {
            return Handles(Resolve(handle).Children);
        }
    }

    public void MarkRunning(AgentTaskNodeHandle handle, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = Resolve(handle);
            if (SetStatus(node, AgentTaskProgressStatus.Running))
            {
                PublishSnapshot();
            }
        }
    }

    public void ReportRetry(string path, int nextAttempt, int maximumAttempts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safePath = string.Concat(path.Where(character => !char.IsControl(character)).Take(160));
        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = ownerAgentSessionId,
            RetryNotice = new RetryNotice
            {
                Attempt = nextAttempt,
                Reason = FormattableString.Invariant($"Task {safePath}: acceptance requested retry; attempt {nextAttempt}/{maximumAttempts}."),
            },
        };
        _ = eventRepository.Append(published, null, null);
        eventBroker.Publish(published);
    }

    public void MarkTerminal(
        AgentTaskNodeHandle handle,
        AgentTaskExecutionStatus status,
        CancellationToken cancellationToken)
    {
        var progressStatus = status switch
        {
            AgentTaskExecutionStatus.Succeeded => AgentTaskProgressStatus.Succeeded,
            AgentTaskExecutionStatus.Failed => AgentTaskProgressStatus.Failed,
            AgentTaskExecutionStatus.Blocked => AgentTaskProgressStatus.Blocked,
            AgentTaskExecutionStatus.Canceled => AgentTaskProgressStatus.Canceled,
            _ => throw new ArgumentException("A committed task result must be terminal.", nameof(status)),
        };

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = Resolve(handle);
            var changed = SetStatus(node, progressStatus);
            if (progressStatus == AgentTaskProgressStatus.Failed)
            {
                changed = BlockPending(node.Children) || changed;
            }

            if (changed)
            {
                PublishSnapshot();
            }
        }
    }

    public void MarkBlocked(AgentTaskNodeHandle handle, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = Resolve(handle);
            var changed = SetStatus(node, AgentTaskProgressStatus.Blocked);
            if (BlockPending(node.Children) || changed)
            {
                PublishSnapshot();
            }
        }
    }

    public IReadOnlyList<AgentTaskNodeHandle> ReplaceChildren(
        AgentTaskNodeHandle handle,
        AgentTaskPayload payload,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = Resolve(handle);
            ReplaceChildrenNodes(node, payload);
            PublishSnapshot();
            return Handles(node.Children);
        }
    }

    public IReadOnlyList<AgentTaskNodeHandle> UpdatePreparedTask(
        AgentTaskNodeHandle handle,
        string description,
        AgentTaskPayload? payload,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = Resolve(handle);
            var changed = node.Description != description;
            node.Description = description;
            if (payload is not null)
            {
                ReplaceChildrenNodes(node, payload);
                changed = true;
            }

            if (changed)
            {
                PublishSnapshot();
            }

            return Handles(node.Children);
        }
    }

    public void MarkRemainingCanceled(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CancelRemaining(_roots))
            {
                PublishSnapshot();
            }
        }
    }

    public void MarkRemainingFailed(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailRemaining(_roots))
            {
                PublishSnapshot();
            }
        }
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<AgentTaskNodeHandle> Handles(
        IReadOnlyList<ProgressNode> nodes) =>
        Array.AsReadOnly(nodes.Select(node => node.Handle).ToArray());

    private static AgentTaskProgressNode BuildSnapshotNode(ProgressNode node)
    {
        var snapshot = new AgentTaskProgressNode
        {
            Name = node.Name,
            Description = node.Description,
            Status = node.Status,
        };
        snapshot.Children.Add(node.Children.Select(BuildSnapshotNode));
        return snapshot;
    }

    private bool SetStatus(ProgressNode node, AgentTaskProgressStatus status)
    {
        if (node.Status == status)
        {
            return false;
        }

        node.Status = status;
        if (status == AgentTaskProgressStatus.Running)
        {
            node.StartedTimestamp = Stopwatch.GetTimestamp();
        }

        WriteDiagnostic(node, status == AgentTaskProgressStatus.Running ? "running" : "terminal");
        return true;
    }

    private bool BlockPending(IEnumerable<ProgressNode> nodes)
    {
        var changed = false;
        foreach (var node in nodes)
        {
            if (node.Status == AgentTaskProgressStatus.Pending)
            {
                _ = SetStatus(node, AgentTaskProgressStatus.Blocked);
                changed = true;
            }

            changed = BlockPending(node.Children) || changed;
        }

        return changed;
    }

    private bool CancelRemaining(IEnumerable<ProgressNode> nodes)
    {
        var changed = false;
        foreach (var node in nodes)
        {
            if (node.Status is AgentTaskProgressStatus.Pending or AgentTaskProgressStatus.Running)
            {
                _ = SetStatus(node, AgentTaskProgressStatus.Canceled);
                changed = true;
            }

            changed = CancelRemaining(node.Children) || changed;
        }

        return changed;
    }

    private bool FailRemaining(IEnumerable<ProgressNode> nodes)
    {
        var changed = false;
        foreach (var node in nodes)
        {
            if (node.Status == AgentTaskProgressStatus.Running)
            {
                _ = SetStatus(node, AgentTaskProgressStatus.Failed);
                changed = true;
            }
            else if (node.Status == AgentTaskProgressStatus.Pending)
            {
                _ = SetStatus(node, AgentTaskProgressStatus.Blocked);
                changed = true;
            }

            changed = FailRemaining(node.Children) || changed;
        }

        return changed;
    }

    private void ReplaceChildrenNodes(ProgressNode node, AgentTaskPayload payload)
    {
        RemoveNodes(node.Children);
        node.Children.Clear();
        if (payload.Tasks is not null)
        {
            node.Children.AddRange(payload.Tasks.Select(BuildNode));
        }
    }

    private ProgressNode BuildNode(AgentTask task)
    {
        var node = new ProgressNode(
            task.Name,
            task.Description,
            task.Payload.Tasks?.Select(BuildNode).ToList() ?? []);
        _nodes.Add(node.Handle, node);
        WriteDiagnostic(node, "scheduled");
        return node;
    }

    private void WriteDiagnostic(ProgressNode node, string operation) =>
        diagnostics.Write(new DiagnosticEvent("task", operation, node.Status == AgentTaskProgressStatus.Failed ? DiagnosticSeverity.Error : DiagnosticSeverity.Information)
        {
            AgentSessionId = ownerAgentSessionId,
            CorrelationId = $"{originToolCallId}/{node.DiagnosticId}",
            Outcome = node.Status.ToString().ToLowerInvariant(),
            DurationMilliseconds = (long)Stopwatch.GetElapsedTime(node.StartedTimestamp).TotalMilliseconds,
        });

    private ProgressNode Resolve(AgentTaskNodeHandle handle) =>
        _nodes.TryGetValue(handle, out var node)
            ? node
            : throw new InvalidOperationException("AgentTask progress handle is stale.");

    private void RemoveNodes(IEnumerable<ProgressNode> nodes)
    {
        foreach (var node in nodes)
        {
            RemoveNodes(node.Children);
            if (node.Status is AgentTaskProgressStatus.Pending or AgentTaskProgressStatus.Running)
            {
                diagnostics.Write(new DiagnosticEvent("task", "terminal", DiagnosticSeverity.Information)
                {
                    AgentSessionId = ownerAgentSessionId,
                    CorrelationId = $"{originToolCallId}/{node.DiagnosticId}",
                    Outcome = "superseded",
                    DurationMilliseconds = (long)Stopwatch.GetElapsedTime(node.StartedTimestamp).TotalMilliseconds,
                });
            }

            _ = _nodes.Remove(node.Handle);
        }
    }

    private void PublishSnapshot()
    {
        var snapshot = BuildSnapshot(checked(++_revision));
        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = ownerAgentSessionId,
            AgentTaskProgressSnapshot = snapshot,
        };
        _ = eventRepository.Append(published, null, null);
        eventBroker.Publish(published);
    }

    private AgentTaskProgressSnapshot BuildSnapshot(ulong revision)
    {
        var snapshot = new AgentTaskProgressSnapshot
        {
            OriginToolCallId = originToolCallId,
            Revision = revision,
        };
        snapshot.RootNodes.Add(_roots.Select(BuildSnapshotNode));
        return snapshot;
    }

    private sealed class ProgressNode(string name, string description, List<ProgressNode> children)
    {
        internal string DiagnosticId { get; } = $"task-{Guid.CreateVersion7():n}";

        internal long StartedTimestamp { get; set; } = Stopwatch.GetTimestamp();

        internal string Name { get; } = name;

        internal string Description { get; set; } = description;

        internal AgentTaskProgressStatus Status { get; set; } = AgentTaskProgressStatus.Pending;

        internal List<ProgressNode> Children { get; } = children;

        internal AgentTaskNodeHandle Handle { get; } = new();
    }
}
