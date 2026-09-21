using System.Text.Json;

namespace Parrot.Store;

// Projects the history of every agent that occupied a scratch directory, the
// current occupant last, so a re-used agent name appends to its predecessors.
internal sealed class AgentHistoryFile(AgentScratchDirectory scratch, IReadOnlyList<string> occupantSessionIds) : IAgentHistoryFile
{
    private const UnixFileMode HistoryFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly Lock _gate = new();
    private readonly string _agentSessionId = occupantSessionIds[^1];

    public string Path => scratch.HistoryPath;

    public void Refresh(IEventRepository repository, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ValidateSession(sessionId);
        try
        {
            ReplaceFromReader(() => [.. occupantSessionIds.SelectMany(repository.AgentHistory)]);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }

    public void ValidateSession(string sessionId)
    {
        if (!string.Equals(_agentSessionId, sessionId, StringComparison.Ordinal))
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
            scratch.Provision();
            ValidateTarget(path);
            var temporary = System.IO.Path.Combine(
                scratch.Root,
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
