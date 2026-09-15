using System.Text.Json;
using Parrot.Agent;

namespace Parrot.Queues;

internal sealed class QueueStore(string directory) : IQueueStore
{
    private const int MaximumFileBytes = 16 << 20;
    private const UnixFileMode DirectoryPermissions =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode FilePermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly QueueStoreGate _gate = new();
    private readonly Dictionary<string, QueueState> _pendingInventoryStates = new(StringComparer.Ordinal);
    private IQueueInventory? _inventory;
    private AgentIdentity? _owner;
    private bool _attachingInventory;
    private bool _disposed;

    public string Directory { get; } = Provision(directory);

    public void AttachInventory(AgentIdentity owner, IQueueInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(inventory);
        _gate.Retain();
        try
        {
            List<QueueState> states;
            _gate.Wait();
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_inventory is not null || _attachingInventory)
                {
                    throw new InvalidOperationException("Queue store inventory is already attached.");
                }

                states = ReadInventoryLocked(owner);
                _owner = owner;
                _attachingInventory = true;
            }
            finally
            {
                _gate.Release();
            }

            try
            {
                inventory.RegisterOwner(states);
            }
            catch
            {
                _gate.WaitAfterDispose();
                try
                {
                    _pendingInventoryStates.Clear();
                    _owner = null;
                    _attachingInventory = false;
                }
                finally
                {
                    _gate.Release();
                }

                throw;
            }

            while (true)
            {
                QueueState[] pending;
                var disposed = false;
                _gate.WaitAfterDispose();
                try
                {
                    if (_disposed)
                    {
                        _pendingInventoryStates.Clear();
                        _attachingInventory = false;
                        disposed = true;
                        pending = [];
                    }
                    else if (_pendingInventoryStates.Count == 0)
                    {
                        _inventory = inventory;
                        _attachingInventory = false;
                        return;
                    }
                    else
                    {
                        pending = [.. _pendingInventoryStates.Values];
                        _pendingInventoryStates.Clear();
                    }
                }
                finally
                {
                    _gate.Release();
                }

                if (disposed)
                {
                    inventory.UnregisterOwner();
                    throw new ObjectDisposedException(nameof(QueueStore));
                }

                foreach (var state in pending)
                {
                    inventory.Update(state);
                }
            }
        }
        finally
        {
            _gate.ReleaseRetention();
        }
    }

    public QueueInfo Create(string name, string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        var path = ResolvePath(name);

        _gate.Wait();

        try
        {
            ThrowIfDisposedLocked();
            var metadata = new QueueMetadata { Name = name, Description = description };
            CreateFile(path, Encode(metadata, []), name);
            return QueueInfo.FromMetadata(path, metadata, 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    public QueueInfo Push(string name, IReadOnlyList<string> items, QueueDirection direction, bool close)
    {
        ArgumentNullException.ThrowIfNull(items);
        direction = ResolvePushDirection(direction);
        var path = ResolvePath(name);

        _gate.Wait();

        try
        {
            ThrowIfDisposedLocked();
            var (metadata, stored) = Read(path, name);
            if (metadata.Closed)
            {
                if (close && items.Count == 0)
                {
                    return QueueInfo.FromMetadata(path, metadata, stored.Count);
                }

                throw new QueueClosedException($"queue: '{name}' is closed");
            }

            if (direction == QueueDirection.Front && items.Count > 0)
            {
                var front = new List<string>(items.Count + stored.Count);

                for (var index = items.Count - 1; index >= 0; index--)
                {
                    front.Add(RequireItem(items[index]));
                }

                front.AddRange(stored);
                stored = front;
            }
            else
            {
                foreach (var item in items)
                {
                    stored.Add(RequireItem(item));
                }
            }

            metadata = close ? metadata with { Closed = true } : metadata;
            Write(path, metadata, stored);
            PublishInventoryLocked(metadata, stored.Count);
            return QueueInfo.FromMetadata(path, metadata, stored.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QueueTakeResult> Take(
        string name,
        int count,
        QueueDirection direction,
        CancellationToken cancellationToken)
    {
        direction = ResolveTakeDirection(count, direction);
        var path = ResolvePath(name);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfDisposedLocked();
            var taken = TakeLocked(path, name, count, direction);
            PublishInventoryLocked(taken.Info);
            return taken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public QueueTryTakeResult TryTake(string name, int count, QueueDirection direction)
    {
        direction = ResolveTakeDirection(count, direction);
        var path = ResolvePath(name);

        if (!_gate.TryWait())
        {
            return new QueueTryTakeResult(false, [], null);
        }

        try
        {
            ThrowIfDisposedLocked();
            var taken = TakeLocked(path, name, count, direction);
            PublishInventoryLocked(taken.Info);
            return new QueueTryTakeResult(true, taken.Items, taken.Info);
        }
        finally
        {
            _gate.Release();
        }
    }

    public QueueInfo Get(string name)
    {
        var path = ResolvePath(name);
        _gate.Wait();

        try
        {
            ThrowIfDisposedLocked();
            var (metadata, items) = Read(path, name);
            return QueueInfo.FromMetadata(path, metadata, items.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<QueueInfo> List()
    {
        _gate.Wait();

        try
        {
            ThrowIfDisposedLocked();
            if (!System.IO.Directory.Exists(Directory))
            {
                return [];
            }

            var result = new List<QueueInfo>();
            var paths = System.IO.Directory.EnumerateFiles(Directory, "*.jsonl")
                .Order(StringComparer.Ordinal);

            foreach (var path in paths)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                ValidateName(name);

                try
                {
                    var (metadata, items) = Read(path, name);
                    result.Add(QueueInfo.FromMetadata(path, metadata, items.Count));
                }
                catch (QueueException failure)
                {
                    throw new QueueException($"queue: could not read '{name}'", failure);
                }
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        IQueueInventory? inventory;
        AgentIdentity? owner;
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            inventory = _inventory;
            owner = _owner;
            _inventory = null;
            _owner = null;
            _pendingInventoryStates.Clear();
        }
        finally
        {
            _gate.Release();
        }

        if (inventory is not null && owner is not null)
        {
            inventory.UnregisterOwner();
        }

        _gate.Dispose();
    }

    private static string Provision(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        if (!Path.IsPathFullyQualified(directory))
        {
            throw new QueueException("queue: absolute directory is required");
        }

        var resolved = Path.GetFullPath(directory);

        try
        {
            _ = System.IO.Directory.CreateDirectory(resolved);

            if (!new DirectoryInfo(resolved).Exists)
            {
                throw new QueueException("queue: path is not a directory");
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(resolved, DirectoryPermissions);
            }

            return resolved;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new QueueException("queue: path is not a directory", failure);
        }
    }

    private static QueueDirection ResolvePushDirection(QueueDirection direction) => direction switch
    {
        QueueDirection.Unspecified => QueueDirection.Back,
        QueueDirection.Front or QueueDirection.Back => direction,
        _ => throw new QueueInvalidDirectionException($"queue: direction '{direction}' must be front or back"),
    };

    private static QueueDirection ResolveTakeDirection(int count, QueueDirection direction)
    {
        if (count <= 0)
        {
            throw new QueueInvalidCountException($"queue: count {count} must be positive");
        }

        return direction switch
        {
            QueueDirection.Unspecified => QueueDirection.Front,
            QueueDirection.Front or QueueDirection.Back => direction,
            _ => throw new QueueInvalidDirectionException($"queue: direction '{direction}' must be front or back"),
        };
    }

    private static string RequireItem(string? item) =>
        item ?? throw new QueueException("queue: items cannot contain null");

    private static void ValidateName(string name)
    {
        if (name.Length == 0 || name[0] == '-' || name[^1] == '-')
        {
            throw new QueueInvalidNameException($"queue: name '{name}' must contain only lowercase ASCII alphanumeric words separated by hyphens");
        }

        var previousHyphen = false;

        foreach (var character in name)
        {
            var hyphen = character == '-';

            if ((!hyphen && (character < 'a' || character > 'z') && (character < '0' || character > '9'))
                || (hyphen && previousHyphen))
            {
                throw new QueueInvalidNameException($"queue: name '{name}' must contain only lowercase ASCII alphanumeric words separated by hyphens");
            }

            previousHyphen = hyphen;
        }
    }

    private static QueueTakeResult TakeLocked(
        string path,
        string name,
        int count,
        QueueDirection direction)
    {
        var (current, items) = Read(path, name);

        if (items.Count == 0)
        {
            if (current.Closed)
            {
                return new QueueTakeResult([], QueueInfo.FromMetadata(path, current, 0));
            }

            throw new QueueEmptyException(QueueInfo.FromMetadata(path, current, 0));
        }

        count = Math.Min(count, items.Count);
        var taken = new List<string>(count);
        var metadata = current;

        if (direction == QueueDirection.Front)
        {
            taken.AddRange(items.GetRange(0, count));
            items.RemoveRange(0, count);
        }
        else
        {
            for (var index = 0; index < count; index++)
            {
                taken.Add(items[items.Count - index - 1]);
            }

            items.RemoveRange(items.Count - count, count);
        }

        Write(path, metadata, items);
        return new QueueTakeResult(taken, QueueInfo.FromMetadata(path, metadata, items.Count));
    }

    private static (QueueMetadata Metadata, List<string> Items) Read(string path, string name)
    {
        FileInfo info;

        try
        {
            info = new FileInfo(path);

            if (!info.Exists)
            {
                throw new QueueNotFoundException($"queue: '{name}' was not found");
            }
        }
        catch (QueueException)
        {
            throw;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new QueueException("queue: could not inspect file", failure);
        }

        if (info.Length > MaximumFileBytes)
        {
            throw new QueueException($"queue: file exceeds {MaximumFileBytes} bytes");
        }

        byte[] data;

        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            throw new QueueNotFoundException($"queue: '{name}' was not found");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new QueueException("queue: could not read file", failure);
        }

        var firstNewline = Array.IndexOf(data, (byte)'\n');

        if (firstNewline < 0)
        {
            throw new QueueException("queue: could not read metadata line");
        }

        try
        {
            var metadata = JsonSerializer.Deserialize(
                data.AsSpan(0, firstNewline), QueuesJsonContext.Default.QueueMetadata)
                ?? throw new QueueException("queue: metadata is null");
            ValidateMetadata(metadata, name);
            var items = new List<string>();
            var offset = firstNewline + 1;

            while (offset < data.Length)
            {
                var newline = Array.IndexOf(data, (byte)'\n', offset);
                var length = newline < 0 ? data.Length - offset : newline - offset;
                var item = JsonSerializer.Deserialize(
                    data.AsSpan(offset, length), QueuesJsonContext.Default.String)
                    ?? throw new QueueException("queue: item is null");
                items.Add(item);

                if (newline < 0)
                {
                    break;
                }

                offset = newline + 1;
            }

            return (metadata, items);
        }
        catch (JsonException failure)
        {
            throw new QueueException("queue: malformed JSONL", failure);
        }
    }

    private static void ValidateMetadata(QueueMetadata metadata, string name)
    {
        if (!string.Equals(metadata.Name, name, StringComparison.Ordinal))
        {
            throw new QueueException("queue: metadata name does not match path");
        }
    }

    private static byte[] Encode(QueueMetadata metadata, IReadOnlyList<string> items)
    {
        using var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, metadata, QueuesJsonContext.Default.QueueMetadata);
        stream.WriteByte((byte)'\n');

        foreach (var item in items)
        {
            JsonSerializer.Serialize(stream, RequireItem(item), QueuesJsonContext.Default.String);
            stream.WriteByte((byte)'\n');
        }

        if (stream.Length > MaximumFileBytes)
        {
            throw new QueueException($"queue: file exceeds {MaximumFileBytes} bytes");
        }

        return stream.ToArray();
    }

    private static void CreateFile(string path, byte[] data, string name)
    {
        var temporary = TemporaryPath(path);

        try
        {
            WriteTemporary(temporary, data);

            try
            {
                File.Move(temporary, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                throw new QueueAlreadyExistsException($"queue: '{name}' already exists");
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void Write(string path, QueueMetadata metadata, IReadOnlyList<string> items)
    {
        var data = Encode(metadata, items);
        var temporary = TemporaryPath(path);

        try
        {
            WriteTemporary(temporary, data);
            File.Move(temporary, path, overwrite: true);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, FilePermissions);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new QueueException("queue: could not persist file", failure);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void WriteTemporary(string path, byte[] data)
    {
        var options = new FileStreamOptions
        {
            Mode = System.IO.FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FilePermissions;
        }

        using var stream = new FileStream(path, options);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }

    private static string TemporaryPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, $".{Path.GetFileName(path)}-{Guid.NewGuid():n}");

    private List<QueueState> ReadInventoryLocked(AgentIdentity owner)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var states = new List<QueueState>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.jsonl")
                     .Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            ValidateName(name);
            var (metadata, items) = Read(path, name);
            if (items.Count > 0)
            {
                states.Add(new(
                    owner.SessionId,
                    owner.Name,
                    owner.ParentSessionId,
                    owner.ParentSessionName,
                    name,
                    metadata.Description ?? string.Empty,
                    items.Count));
            }
        }

        return states;
    }

    private void PublishInventoryLocked(QueueMetadata metadata, int itemCount) =>
        PublishInventoryLocked(QueueInfo.FromMetadata(string.Empty, metadata, itemCount));

    private void PublishInventoryLocked(QueueInfo info)
    {
        if (_owner is null)
        {
            return;
        }

        var state = new QueueState(
            _owner.SessionId,
            _owner.Name,
            _owner.ParentSessionId,
            _owner.ParentSessionName,
            info.Name,
            info.Description,
            info.Size);
        if (_inventory is null)
        {
            if (_attachingInventory)
            {
                _pendingInventoryStates[info.Name] = state;
            }

            return;
        }

        _inventory.Update(state);
    }

    private void ThrowIfDisposedLocked() => ObjectDisposedException.ThrowIf(_disposed, this);

    private string ResolvePath(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ValidateName(name);
        return Path.Combine(Directory, name + ".jsonl");
    }
}
