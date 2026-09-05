using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.AgentTasks;

internal sealed class AgentTaskProgress(
    EventBroker eventBroker,
    EventRepository eventRepository,
    string ownerAgentSessionId,
    string originToolCallId)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<NodeHandle, ProgressNode> _nodes = [];
    private readonly List<ProgressNode> _roots = [];
    private ulong _revision;
    private bool _initialized;

    internal IReadOnlyList<NodeHandle> Initialize(
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

    internal AgentTaskProgressSnapshot CurrentSnapshot()
    {
        lock (_gate)
        {
            return BuildSnapshot(_revision);
        }
    }

    internal IReadOnlyList<NodeHandle> EnsureInitialized(
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

    internal IReadOnlyList<NodeHandle> GetChildren(NodeHandle handle)
    {
        lock (_gate)
        {
            return Handles(Resolve(handle).Children);
        }
    }

    internal void MarkRunning(NodeHandle handle, CancellationToken cancellationToken)
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

    internal void MarkTerminal(
        NodeHandle handle,
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

    internal void MarkBlocked(NodeHandle handle, CancellationToken cancellationToken)
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

    internal IReadOnlyList<NodeHandle> ReplaceChildren(
        NodeHandle handle,
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

    internal IReadOnlyList<NodeHandle> UpdatePreparedTask(
        NodeHandle handle,
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

    internal void MarkRemainingCanceled(CancellationToken cancellationToken)
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

    internal void MarkRemainingFailed(CancellationToken cancellationToken)
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

    private static System.Collections.ObjectModel.ReadOnlyCollection<NodeHandle> Handles(
        IReadOnlyList<ProgressNode> nodes) =>
        Array.AsReadOnly(nodes.Select(node => node.Handle).ToArray());

    private static bool SetStatus(ProgressNode node, AgentTaskProgressStatus status)
    {
        if (node.Status == status)
        {
            return false;
        }

        node.Status = status;
        return true;
    }

    private static bool BlockPending(IEnumerable<ProgressNode> nodes)
    {
        var changed = false;
        foreach (var node in nodes)
        {
            if (node.Status == AgentTaskProgressStatus.Pending)
            {
                node.Status = AgentTaskProgressStatus.Blocked;
                changed = true;
            }

            changed = BlockPending(node.Children) || changed;
        }

        return changed;
    }

    private static bool CancelRemaining(IEnumerable<ProgressNode> nodes)
    {
        var changed = false;
        foreach (var node in nodes)
        {
            if (node.Status is AgentTaskProgressStatus.Pending or AgentTaskProgressStatus.Running)
            {
                node.Status = AgentTaskProgressStatus.Canceled;
                changed = true;
            }

            changed = CancelRemaining(node.Children) || changed;
        }

        return changed;
    }

    private static bool FailRemaining(IEnumerable<ProgressNode> nodes)
    {
        var changed = false;
        foreach (var node in nodes)
        {
            if (node.Status == AgentTaskProgressStatus.Running)
            {
                node.Status = AgentTaskProgressStatus.Failed;
                changed = true;
            }
            else if (node.Status == AgentTaskProgressStatus.Pending)
            {
                node.Status = AgentTaskProgressStatus.Blocked;
                changed = true;
            }

            changed = FailRemaining(node.Children) || changed;
        }

        return changed;
    }

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
        return node;
    }

    private ProgressNode Resolve(NodeHandle handle) =>
        _nodes.TryGetValue(handle, out var node)
            ? node
            : throw new InvalidOperationException("AgentTask progress handle is stale.");

    private void RemoveNodes(IEnumerable<ProgressNode> nodes)
    {
        foreach (var node in nodes)
        {
            RemoveNodes(node.Children);
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

    internal sealed class NodeHandle;

    private sealed class ProgressNode(string name, string description, List<ProgressNode> children)
    {
        internal string Name { get; } = name;

        internal string Description { get; set; } = description;

        internal AgentTaskProgressStatus Status { get; set; } = AgentTaskProgressStatus.Pending;

        internal List<ProgressNode> Children { get; } = children;

        internal NodeHandle Handle { get; } = new();
    }
}
