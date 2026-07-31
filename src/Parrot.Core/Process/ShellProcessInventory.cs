namespace Parrot.Process;

internal sealed class ShellProcessInventory : IDisposable
{
    private readonly ShellProcessInventoryFeed _feed = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ActiveShellProcessState> _processes = new(StringComparer.Ordinal);
    private ulong _revision;

    public string InstanceId { get; } = $"process-inventory-{Guid.CreateVersion7():n}";

    public YieldedShellProcess Publish(ActiveShellProcessState process)
    {
        ShellProcessInventorySnapshot snapshot;
        ulong visibleRevision;

        lock (_gate)
        {
            if (!_processes.TryAdd(process.ProcessId, process))
            {
                throw new InvalidOperationException($"Shell process '{process.ProcessId}' is already visible.");
            }

            visibleRevision = ++_revision;
            snapshot = CaptureLocked();
            _feed.Publish(snapshot);
        }

        return new YieldedShellProcess(process.ProcessId, process.Name, InstanceId, visibleRevision);
    }

    public void Remove(string processId)
    {
        lock (_gate)
        {
            if (!_processes.Remove(processId))
            {
                return;
            }

            _revision++;
            _feed.Publish(CaptureLocked());
        }
    }

    public ShellProcessInventorySubscription Subscribe()
    {
        lock (_gate)
        {
            return _feed.Subscribe(CaptureLocked());
        }
    }

    public void Dispose() => _feed.Dispose();

    private ShellProcessInventorySnapshot CaptureLocked() =>
        new(
            InstanceId,
            _revision,
            [.. _processes.Values.OrderBy(process => process.ProcessId, StringComparer.Ordinal)]);
}
