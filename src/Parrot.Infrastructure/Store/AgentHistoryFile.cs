using System.Text.Json;

namespace Parrot.Store;

// Projects the history of every agent that occupied a scratch directory, the
// current occupant last, so a re-used agent name appends to its predecessors.
//
// Only the current occupant gains entries, and always after the ones already
// projected, so a refresh appends what is new. Whenever the file may no longer
// be what was last written, it is rebuilt whole instead.
internal sealed class AgentHistoryFile(AgentScratchDirectory scratch, IReadOnlyList<string> occupantSessionIds) : IAgentHistoryFile
{
    private const UnixFileMode HistoryFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly Lock _gate = new();
    private readonly string _agentSessionId = occupantSessionIds[^1];
    private long _historySequence;
    private long _requestEventSequence;
    private long? _writtenLength;

    public string Path => scratch.HistoryPath;

    public void Refresh(IEventRepository repository, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ValidateSession(sessionId);
        try
        {
            lock (_gate)
            {
                if (_writtenLength is not { } writtenLength || !IsWritten(writtenLength))
                {
                    RebuildLocked(repository);
                    return;
                }

                var entries = repository.AgentHistoryAfter(_agentSessionId, _historySequence, _requestEventSequence);
                if (entries.Count == 0)
                {
                    return;
                }

                _writtenLength = null;
                AppendEntries(Path, entries);
                Advance(entries);
                _writtenLength = new FileInfo(Path).Length;
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }

    public void Rebuild(IEventRepository repository, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ValidateSession(sessionId);
        try
        {
            lock (_gate)
            {
                RebuildLocked(repository);
            }
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
        lock (_gate)
        {
            _writtenLength = null;
            ReplaceLocked(entries);
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
        WriteEntries(stream, entries);
    }

    private static void AppendEntries(string path, IReadOnlyList<AgentHistoryEntry> entries)
    {
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        });
        WriteEntries(stream, entries);
    }

    private static void WriteEntries(FileStream stream, IReadOnlyList<AgentHistoryEntry> entries)
    {
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

    // Anything but the regular file last written, at the length it was left,
    // was changed by someone else and is no longer safe to append to.
    private bool IsWritten(long writtenLength)
    {
        var file = new FileInfo(Path);
        return file.Exists
            && (file.Attributes & FileAttributes.ReparsePoint) == 0
            && file.Length == writtenLength;
    }

    private void RebuildLocked(IEventRepository repository)
    {
        _writtenLength = null;
        _historySequence = 0;
        _requestEventSequence = 0;
        var occupants = occupantSessionIds.Select(repository.AgentHistory).ToArray();
        ReplaceLocked([.. occupants.SelectMany(static entries => entries)]);
        Advance(occupants[^1]);
        _writtenLength = new FileInfo(Path).Length;
    }

    private void ReplaceLocked(IReadOnlyList<AgentHistoryEntry> entries)
    {
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

    private void Advance(IReadOnlyList<AgentHistoryEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry is AgentHistoryRequestEntry request)
            {
                _requestEventSequence = Math.Max(_requestEventSequence, request.EventSequence);
            }
            else
            {
                _historySequence = Math.Max(_historySequence, entry.Sequence);
            }
        }
    }
}
