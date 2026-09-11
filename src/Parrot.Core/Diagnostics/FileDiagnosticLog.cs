using System.Globalization;
using System.Text;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Diagnostics;

internal sealed class FileDiagnosticLog : IDiagnosticLog
{
    private const long MaximumFileBytes = 10 * 1024 * 1024;
    private const int MaximumFieldCharacters = 256;
    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly string _instanceId;
    private readonly string? _userSessionId;
    private readonly Action<string> _writeWarning;
    private readonly TimeProvider _timeProvider;
    private FileStream? _stream;
    private bool _disabled;

    private FileDiagnosticLog(
        string path,
        string instanceId,
        string? userSessionId,
        TextWriter error,
        TimeProvider timeProvider,
        FileMode mode)
    {
        _path = path;
        _instanceId = instanceId;
        _userSessionId = userSessionId;
        _writeWarning = error.WriteLine;
        _timeProvider = timeProvider;
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new IOException("The log directory is unavailable."));
            _stream = Open(mode);
        }
        catch (Exception failure) when (IsSinkFailure(failure))
        {
            Disable();
        }
    }

    public static string CreateInstanceId() =>
        string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Environment.ProcessId}-{Guid.NewGuid():N}");

    public static IDiagnosticLog OpenGlobal(
        StatePaths paths,
        string instanceId,
        TextWriter error,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(paths);
        RequireInstanceId(instanceId);
        return new FileDiagnosticLog(
            Path.Combine(paths.LogDirectory, $"parrot-{instanceId}.log"),
            instanceId,
            null,
            error,
            timeProvider,
            FileMode.CreateNew);
    }

    /// <summary>The caller must hold session activation ownership until this sink and all its producers are closed.</summary>
    public static IDiagnosticLog OpenSession(
        UserSessionResources resources,
        string instanceId,
        TextWriter error,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(resources);
        RequireInstanceId(instanceId);
        return new FileDiagnosticLog(
            resources.LogPath, instanceId, resources.Id.Value, error, timeProvider, FileMode.Append);
    }

    public void Write(DiagnosticEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (_disabled || _stream is null)
            {
                return;
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(Format(entry));
                if (_stream.Length + bytes.Length > MaximumFileBytes)
                {
                    Rotate();
                }

                _stream.Write(bytes);
                _stream.Flush();
            }
            catch (Exception failure) when (IsSinkFailure(failure))
            {
                Disable();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                _stream?.Dispose();
                _stream = null;
                _disabled = true;
            }
            catch (Exception failure) when (IsSinkFailure(failure))
            {
                Disable();
            }
        }
    }

    private static void Append(StringBuilder text, string key, string? value)
    {
        if (value is null)
        {
            return;
        }

        _ = text.Append(' ').Append(key).Append("=\"");
        var length = Math.Min(value.Length, MaximumFieldCharacters);
        if (length < value.Length && length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        foreach (var character in value.AsSpan(0, length))
        {
            switch (character)
            {
                case '"':
                case '\\':
                    _ = text.Append('\\').Append(character);
                    break;
                default:
                    if (char.IsControl(character) || char.GetUnicodeCategory(character) is UnicodeCategory.Format
                        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                    {
                        _ = text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        _ = text.Append(character);
                    }

                    break;
            }
        }

        if (length < value.Length)
        {
            _ = text.Append("[truncated]");
        }

        _ = text.Append('"');
    }

    private static bool IsSinkFailure(Exception failure) =>
        failure is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ObjectDisposedException or NotSupportedException;

    private static void RequireInstanceId(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (instanceId.Length > MaximumFieldCharacters
            || instanceId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("A diagnostic instance id must contain only ASCII letters, digits and hyphens.", nameof(instanceId));
        }
    }

    private string Format(DiagnosticEvent entry)
    {
        var severity = entry.Severity switch
        {
            DiagnosticSeverity.Warning => "WARN",
            DiagnosticSeverity.Error => "ERROR",
            _ => "INFO",
        };
        var text = new StringBuilder();
        _ = text.Append(_timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)).Append(' ').Append(severity);
        Append(text, "category", entry.Category);
        Append(text, "event", entry.Operation);
        Append(text, "instance", _instanceId);
        Append(text, "session", _userSessionId ?? entry.UserSessionId);
        Append(text, "agent", entry.AgentSessionId);
        Append(text, "correlation", entry.CorrelationId);
        Append(text, "request", entry.RequestId);
        Append(text, "transport", entry.Transport);
        Append(text, "provider", entry.ProviderId);
        Append(text, "model", entry.ModelId);
        Append(text, "tool", entry.ToolName);
        Append(text, "outcome", entry.Outcome);
        Append(text, "duration_ms", entry.DurationMilliseconds?.ToString(CultureInfo.InvariantCulture));
        Append(text, "count", entry.Count?.ToString(CultureInfo.InvariantCulture));
        Append(text, "error", entry.ErrorCode);
        return text.Append('\n').ToString();
    }

    private void Rotate()
    {
        _stream?.Dispose();
        _stream = null;
        File.Delete(_path + ".3");
        for (var backup = 2; backup >= 1; backup--)
        {
            var source = _path + "." + backup.ToString(CultureInfo.InvariantCulture);
            if (File.Exists(source))
            {
                File.Move(source, _path + "." + (backup + 1).ToString(CultureInfo.InvariantCulture));
            }
        }

        File.Move(_path, _path + ".1");
        _stream = Open(FileMode.CreateNew);
    }

    private FileStream Open(FileMode mode) => new(_path, mode, FileAccess.Write, FileShare.Read);

    private void Disable()
    {
        _disabled = true;
        try
        {
            _stream?.Dispose();
        }
        catch (Exception failure) when (IsSinkFailure(failure))
        {
        }

        _stream = null;
        try
        {
            _writeWarning("parrot: operational logging disabled for one log scope because its log could not be written.");
        }
        catch (Exception)
        {
        }
    }
}
