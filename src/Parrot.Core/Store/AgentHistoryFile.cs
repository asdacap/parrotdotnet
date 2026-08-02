using System.Text.Json;

namespace Parrot.Store;

internal sealed class AgentHistoryFile(string path, Lock gate)
{
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode HistoryFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public string Path { get; } = path;

    public void Replace(IReadOnlyList<AgentHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Replace(() => entries);
    }

    public void Replace(Func<IReadOnlyList<AgentHistoryEntry>> readEntries)
    {
        ArgumentNullException.ThrowIfNull(readEntries);
        lock (gate)
        {
            var entries = readEntries();
            var directory = System.IO.Path.GetDirectoryName(Path)
                ?? throw new InvalidOperationException("An agent history file must have a parent directory.");
            ProvisionDirectory(directory);
            ValidateTarget(Path);
            var temporary = System.IO.Path.Combine(
                directory,
                $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():n}.tmp");
            try
            {
                WriteTemporary(temporary, entries);
                File.Move(temporary, Path, overwrite: true);
                SetFileMode(Path);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    private static void ProvisionDirectory(string path)
    {
        if (System.IO.Path.Exists(path) && !Directory.Exists(path))
        {
            throw new InvalidOperationException("An agent history directory is not a directory.");
        }

        var directory = Directory.CreateDirectory(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("An agent history directory cannot be a symbolic link.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, DirectoryMode);
        }
    }

    private static void ValidateTarget(string path)
    {
        if (!System.IO.Path.Exists(path))
        {
            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            File.Delete(path);
        }
    }

    private static void WriteTemporary(string path, IReadOnlyList<AgentHistoryEntry> entries)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = HistoryFileMode;
        }

        using var stream = new FileStream(path, options);
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            JsonSerializer.Serialize(stream, entry, AgentHistoryJsonContext.Default.AgentHistoryEntry);
            stream.WriteByte((byte)'\n');
        }

        stream.Flush(flushToDisk: true);
    }

    private static void SetFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, HistoryFileMode);
        }
    }
}
