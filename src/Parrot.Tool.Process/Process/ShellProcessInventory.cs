using Parrot.Agent;

namespace Parrot.Process;

internal sealed class ShellProcessInventory(AgentIdentity identity) : IDisposable
{
    private readonly ShellProcessInventoryFeed _feed = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ActiveShellProcessState> _processes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompletedShellProcessState> _completedProcesses = new(StringComparer.Ordinal);
    private ulong _revision;
    private bool _disposed;

    public string InstanceId { get; } = $"process-inventory-{Guid.CreateVersion7():n}";

    public YieldedShellProcess Publish(ActiveShellProcessState process)
    {
        ulong visibleRevision;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!string.Equals(process.OwnerAgentSessionId, identity.SessionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Shell process owner '{process.OwnerAgentSessionId}' does not match inventory owner '{identity.SessionId}'.");
            }

            if (_completedProcesses.ContainsKey(process.ProcessId) || !_processes.TryAdd(process.ProcessId, process))
            {
                throw new InvalidOperationException($"Shell process '{process.ProcessId}' is already visible.");
            }

            visibleRevision = ++_revision;
            _feed.Publish(CaptureLocked());
        }

        return new YieldedShellProcess(process.ProcessId, process.Name, InstanceId, visibleRevision, null, null);
    }

    public void Complete(string processId, long? elapsedMilliseconds)
    {
        lock (_gate)
        {
            if (_disposed || !_processes.Remove(processId))
            {
                return;
            }

            _completedProcesses[processId] = new CompletedShellProcessState(processId, elapsedMilliseconds);
            _revision++;
            _feed.Publish(CaptureLocked());
        }
    }

    public ShellProcessInventorySnapshot Capture()
    {
        lock (_gate)
        {
            return CaptureLocked();
        }
    }

    public IShellProcessInventorySubscription Subscribe()
    {
        lock (_gate)
        {
            return _feed.Subscribe(CaptureLocked());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _processes.Clear();
            _completedProcesses.Clear();
            _revision++;
            _feed.Publish(CaptureLocked());
            _feed.Dispose();
        }
    }

    private ShellProcessInventorySnapshot CaptureLocked() =>
        new(
            identity.SessionId,
            InstanceId,
            _revision,
            _disposed,
            [.. _processes.Values.OrderBy(process => process.ProcessId, StringComparer.Ordinal)],
            [.. _completedProcesses.Values.OrderBy(process => process.ProcessId, StringComparer.Ordinal)]);
}
