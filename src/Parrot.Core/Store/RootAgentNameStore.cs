using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Parrot.Store;

// Reserves the durable, global friendly name of a user session's root agent.
// Each mutation appends a version through link(), whose no-overwrite behavior
// remains atomic when the state directory is shared over NFS.
internal sealed partial class RootAgentNameStore(
    string stateDirectory,
    string hostKey,
    int processId,
    Func<int, bool> processIsAlive,
    SessionIndex sessions)
{
    private const int FileExistsError = 17;
    private const string FirstName = "main";

    public RootAgentNameReservationToken Reserve(string sessionId, string requestedName)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(requestedName);

        if (requestedName.Length > 0)
        {
            ValidateName(requestedName);
            return ReserveExact(sessionId, requestedName);
        }

        for (var suffix = 1; ; suffix++)
        {
            var candidate = suffix == 1 ? FirstName : $"{FirstName}-{suffix}";

            while (true)
            {
                var result = ReserveCandidate(sessionId, candidate, exact: false);

                if (result.Reservation is not null)
                {
                    return result.Reservation;
                }

                if (!result.Contended)
                {
                    break;
                }
            }
        }
    }

    public void Release(RootAgentNameReservationToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (!token.Acquired || HasPublishedName(token.Name))
        {
            return;
        }

        var directory = DirectoryFor(token.Name);
        var chain = LoadChain(directory);

        if (chain.Current is not { } current
            || current.Version != token.Version
            || !string.Equals(current.SessionId, token.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        _ = Append(directory, chain.HighestVersion + 1, new RootAgentNameReservation
        {
            Version = chain.HighestVersion + 1,
            SessionId = string.Empty,
            HostKey = string.Empty,
            ProcessId = 0,
        });
    }

    private static ReservationChain LoadChain(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new ReservationChain(0, null);
        }

        var versions = Directory.EnumerateFiles(directory, "v*.json")
            .Select(file => new VersionedFile(file, Version(file)))
            .Where(entry => entry.Version is not null)
            .Select(entry => new VersionedFile(entry.File, entry.Version.GetValueOrDefault()))
            .OrderByDescending(entry => entry.Version)
            .ToList();

        RootAgentNameReservation? current = null;

        foreach (var entry in versions)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize(
                    File.ReadAllText(entry.File), StoreJsonContext.Default.RootAgentNameReservation);

                if (parsed is not null && parsed.Version == entry.Version)
                {
                    current = parsed;
                    break;
                }
            }
            catch (JsonException)
            {
                // A writer may have failed after linking bad bytes. Its number
                // remains consumed, while the preceding valid state survives.
            }
        }

        return new ReservationChain(
            versions.Count == 0 ? 0 : versions[0].Version.GetValueOrDefault(),
            current);
    }

    private static int? Version(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);

        return stem.Length > 1
            && stem[0] == 'v'
            && int.TryParse(
                stem.AsSpan(1),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var version)
            && version > 0
                ? version
                : null;
    }

    private static bool Append(string directory, int version, RootAgentNameReservation record)
    {
        var staged = Path.Combine(directory, $"v{version}.staging-{Environment.ProcessId}-{Guid.NewGuid():n}");
        var target = Path.Combine(directory, $"v{version}.json");

        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, record, StoreJsonContext.Default.RootAgentNameReservation);
                stream.Flush(flushToDisk: true);
            }

            if (AtomicLink.Create(staged, target) == 0)
            {
                return true;
            }

            var error = Marshal.GetLastPInvokeError();

            return error == FileExistsError
                ? false
                : throw new IOException(
                    $"Failed to reserve root agent name version {version}.",
                    new Win32Exception(error));
        }
        finally
        {
            File.Delete(staged);
        }
    }

    private static void ValidateName(string name)
    {
        if (string.Equals(name, FirstName, StringComparison.Ordinal))
        {
            return;
        }

        const string prefix = "main-";
        var valid = name.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(
                name.AsSpan(prefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var suffix)
            && suffix >= 2;

        if (!valid)
        {
            throw new ArgumentException("Root agent names must be main or main-N for N >= 2.", nameof(name));
        }
    }

    private RootAgentNameReservationToken ReserveExact(string sessionId, string name)
    {
        while (true)
        {
            var result = ReserveCandidate(sessionId, name, exact: true);

            if (result.Reservation is not null)
            {
                return result.Reservation;
            }

            if (!result.Contended)
            {
                throw new InvalidOperationException(
                    $"Root agent name '{name}' is not owned by user session '{sessionId}'.");
            }
        }
    }

    private ReservationAttempt ReserveCandidate(string sessionId, string name, bool exact)
    {
        var publishedOwner = PublishedOwner(name);

        if (publishedOwner is not null && !string.Equals(publishedOwner, sessionId, StringComparison.Ordinal))
        {
            return new ReservationAttempt(null, false);
        }

        var directory = DirectoryFor(name);
        _ = Directory.CreateDirectory(directory);
        var chain = LoadChain(directory);
        var current = chain.Current;

        if (current is not null && current.SessionId.Length > 0)
        {
            if (string.Equals(current.SessionId, sessionId, StringComparison.Ordinal))
            {
                return new ReservationAttempt(
                    new RootAgentNameReservationToken(name, sessionId, current.Version, false), false);
            }

            if (publishedOwner is not null || !CanReclaim(current))
            {
                return new ReservationAttempt(null, false);
            }
        }

        // A durable metadata record needs no pending claim to retain its own
        // name, but an exact request still verifies an inconsistent chain.
        if (publishedOwner is not null)
        {
            return exact
                ? new ReservationAttempt(
                    new RootAgentNameReservationToken(name, sessionId, 0, false), false)
                : new ReservationAttempt(null, false);
        }

        var version = chain.HighestVersion + 1;
        var record = new RootAgentNameReservation
        {
            Version = version,
            SessionId = sessionId,
            HostKey = hostKey,
            ProcessId = processId,
        };

        if (!Append(directory, version, record))
        {
            // The caller retries from the state freshly read by the next probe;
            // it must never overwrite the contender's version.
            return new ReservationAttempt(null, true);
        }

        return new ReservationAttempt(
            new RootAgentNameReservationToken(name, sessionId, version, true), false);
    }

    private string? PublishedOwner(string name)
    {
        string? owner = null;

        foreach (var meta in sessions.List())
        {
            if (!string.Equals(meta.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (owner is not null && !string.Equals(owner, meta.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Published root agent name '{name}' has multiple owners.");
            }

            owner = meta.Id;
        }

        return owner;
    }

    private bool HasPublishedName(string name) => PublishedOwner(name) is not null;

    private bool CanReclaim(RootAgentNameReservation reservation) =>
        string.Equals(reservation.HostKey, hostKey, StringComparison.Ordinal)
        && !processIsAlive(reservation.ProcessId);

    private string DirectoryFor(string name)
    {
        ValidateName(name);
        return Path.Combine(stateDirectory, "session-names", name);
    }

    private sealed partial class AtomicLink
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
        [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        public static partial int Create(string existingPath, string newPath);
    }

    private sealed record ReservationChain(int HighestVersion, RootAgentNameReservation? Current);

    private sealed record VersionedFile(string File, int? Version);

    private sealed record ReservationAttempt(RootAgentNameReservationToken? Reservation, bool Contended);
}
