using System.ComponentModel;
using System.Text.Json;

namespace Parrot.Queues;

internal sealed class QueueStore(string directory) : IDisposable
{
    private const int MaximumFileBytes = 16 << 20;
    private const int LockPollMilliseconds = 10;
    private const int LockTimeoutSeconds = 10;
    private const int ErrorFileNotFound = 2;
    private const int ErrorAlreadyExists = 17;
    private const int ErrorWindowsAlreadyExists = 183;
    private const UnixFileMode DirectoryPermissions =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode FilePermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Directory { get; } = Provision(directory);

    public QueueInfo Create(string name, string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        var path = ResolvePath(name);

        _gate.Wait();

        try
        {
            var metadata = new QueueMetadata { Name = name, Description = description };
            CreateFile(path, Encode(metadata, []), name);
            return ToInfo(path, metadata, 0);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public QueueInfo Push(string name, IReadOnlyList<string> items, QueueDirection direction)
    {
        ArgumentNullException.ThrowIfNull(items);
        direction = ResolvePushDirection(direction);
        var path = ResolvePath(name);

        _gate.Wait();

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(LockTimeoutSeconds));
            using var held = AcquireFileLockSynchronously(path, timeout.Token);
            var (metadata, stored) = Read(path, name);

            if (direction == QueueDirection.Front && items.Count > 0)
            {
                metadata = metadata with { DeliveryId = null };
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

            Write(path, metadata, stored);
            return ToInfo(path, metadata, stored.Count);
        }
        catch (OperationCanceledException failure)
        {
            throw new QueueException("queue: timed out acquiring lock", failure);
        }
        finally
        {
            _ = _gate.Release();
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
            using var held = await AcquireFileLockAsync(path, cancellationToken).ConfigureAwait(false);
            return TakeLocked(path, name, count, direction);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public QueueTryTakeResult TryTake(string name, int count, QueueDirection direction)
    {
        direction = ResolveTakeDirection(count, direction);
        var path = ResolvePath(name);

        if (!_gate.Wait(0))
        {
            return new QueueTryTakeResult(false, [], null);
        }

        try
        {
            using var held = TryAcquireFileLock(path);

            if (held is null)
            {
                return new QueueTryTakeResult(false, [], null);
            }

            var taken = TakeLocked(path, name, count, direction);
            return new QueueTryTakeResult(true, taken.Items, taken.Info);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public QueueInfo Get(string name)
    {
        var path = ResolvePath(name);
        _gate.Wait();

        try
        {
            var (metadata, items) = Read(path, name);
            return ToInfo(path, metadata, items.Count);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public IReadOnlyList<QueueInfo> List()
    {
        _gate.Wait();

        try
        {
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
                    result.Add(ToInfo(path, metadata, items.Count));
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
            _ = _gate.Release();
        }
    }

    public QueueInfo Monitor(string name, bool enabled)
    {
        var path = ResolvePath(name);
        _gate.Wait();

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(LockTimeoutSeconds));
            using var held = AcquireFileLockSynchronously(path, timeout.Token);
            var (current, items) = Read(path, name);
            var metadata = current with { Monitored = enabled };
            Write(path, metadata, items);
            return ToInfo(path, metadata, items.Count);
        }
        catch (OperationCanceledException failure)
        {
            throw new QueueException("queue: timed out acquiring lock", failure);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public async Task<bool> DeliverMonitored(
        Func<QueueNotification, CancellationToken, Task<bool>> deliver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return false;
            }

            var paths = System.IO.Directory.EnumerateFiles(Directory, "*.jsonl")
                .Order(StringComparer.Ordinal);

            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(path);
                ValidateName(name);
                using var held = await AcquireFileLockAsync(path, cancellationToken).ConfigureAwait(false);
                var (current, items) = Read(path, name);

                if (!current.Monitored || items.Count == 0)
                {
                    continue;
                }

                var metadata = current;

                if (string.IsNullOrEmpty(metadata.DeliveryId))
                {
                    metadata = metadata with { DeliveryId = $"qnt-{Guid.CreateVersion7():n}" };
                    Write(path, metadata, items);
                }

                var notification = new QueueNotification(metadata.DeliveryId ?? string.Empty, name, items[0]);

                if (!await deliver(notification, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                items.RemoveAt(0);
                Write(path, metadata with { DeliveryId = null }, items);
                return true;
            }

            return false;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

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
            throw new QueueEmptyException(ToInfo(path, current, 0));
        }

        count = Math.Min(count, items.Count);
        var taken = new List<string>(count);
        var metadata = current;

        if (direction == QueueDirection.Front)
        {
            taken.AddRange(items.GetRange(0, count));
            items.RemoveRange(0, count);
            metadata = metadata with { DeliveryId = null };
        }
        else
        {
            for (var index = 0; index < count; index++)
            {
                taken.Add(items[items.Count - index - 1]);
            }

            items.RemoveRange(items.Count - count, count);

            if (items.Count == 0)
            {
                metadata = metadata with { DeliveryId = null };
            }
        }

        Write(path, metadata, items);
        return new QueueTakeResult(taken, ToInfo(path, metadata, items.Count));
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

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    File.Move(temporary, path, overwrite: false);
                    return;
                }
                catch (IOException) when (File.Exists(path))
                {
                    throw new QueueAlreadyExistsException($"queue: '{name}' already exists");
                }
            }

            if (QueueNative.Link(temporary, path) == 0)
            {
                return;
            }

            var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();

            if (error == ErrorAlreadyExists)
            {
                throw new QueueAlreadyExistsException($"queue: '{name}' already exists");
            }

            throw new QueueException("queue: could not publish queue", new Win32Exception(error));
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

    private static QueueInfo ToInfo(string path, QueueMetadata metadata, int size) =>
        new(path, metadata.Name, metadata.Description ?? string.Empty, size, metadata.Monitored);

    private static QueueFileLock AcquireFileLockSynchronously(
        string path,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var held = TryAcquireFileLock(path);

            if (held is not null)
            {
                return held;
            }

            _ = cancellationToken.WaitHandle.WaitOne(LockPollMilliseconds);
        }
    }

    private static async Task<QueueFileLock> AcquireFileLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var held = TryAcquireFileLock(path);

            if (held is not null)
            {
                return held;
            }

            await Task.Delay(LockPollMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    private static QueueFileLock? TryAcquireFileLock(string path)
    {
        var lockPath = path + ".lock";

        if (OperatingSystem.IsWindows())
        {
            if (QueueNative.CreateDirectory(lockPath, 0))
            {
                return new QueueFileLock(lockPath);
            }

            var windowsError = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();

            return windowsError == ErrorWindowsAlreadyExists
                ? null
                : throw new QueueException(
                    "queue: could not acquire lock", new Win32Exception(windowsError));
        }

        if (QueueNative.MakeDirectory(lockPath, Convert.ToUInt32(448)) == 0)
        {
            return new QueueFileLock(lockPath);
        }

        var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();

        return error switch
        {
            ErrorAlreadyExists => null,
            ErrorFileNotFound => new QueueFileLock(string.Empty),
            _ => throw new QueueException("queue: could not acquire lock", new Win32Exception(error)),
        };
    }

    private string ResolvePath(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ValidateName(name);
        return Path.Combine(Directory, name + ".jsonl");
    }
}
