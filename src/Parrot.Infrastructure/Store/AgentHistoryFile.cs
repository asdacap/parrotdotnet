using System.Text.Json;

namespace Parrot.Store;

internal sealed class AgentHistoryFile(UserSessionResources resources, string agentSessionId) : IAgentHistoryFile
{
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode HistoryFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly Lock _gate = new();

    public string Path => resources.AgentHistoryFile(agentSessionId);

    public void Refresh(IEventRepository repository, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ValidateSession(sessionId);
        try
        {
            ReplaceFromReader(() => repository.AgentHistory(agentSessionId));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }

    public void ValidateSession(string sessionId)
    {
        if (!string.Equals(agentSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("History projection belongs to another agent session.");
        }
    }

    public void ReplaceEntries(IReadOnlyList<AgentHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ReplaceFromReader(() => entries);
    }

    public void ReplaceFromReader(Func<IReadOnlyList<AgentHistoryEntry>> readEntries)
    {
        ArgumentNullException.ThrowIfNull(readEntries);
        lock (_gate)
        {
            var entries = readEntries();
            var path = Path;
            var directory = System.IO.Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("An agent history file must have a parent directory.");
            ProvisionDirectory(directory);
            ValidateTarget(path);
            var temporary = System.IO.Path.Combine(
                directory,
                $".{System.IO.Path.GetFileName(path)}.{Guid.NewGuid():n}.tmp");
            try
            {
                WriteTemporary(temporary, entries);
                File.Move(temporary, path, overwrite: true);
                SetFileMode(path);
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
