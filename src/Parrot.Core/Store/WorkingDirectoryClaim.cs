using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Parrot.Store;

internal sealed partial class WorkingDirectoryClaim
{
    private const int SchemaVersion = 2;
    private readonly RuntimeIdentity _runtime;

    public WorkingDirectoryClaim(string stateDirectory, string hostKey)
        : this(stateDirectory, RuntimeIdentityCapture.Capture(hostKey))
    {
    }

    internal WorkingDirectoryClaim(string stateDirectory, RuntimeIdentity runtime)
    {
        StateDirectory = stateDirectory;
        _runtime = runtime;
    }

    public string OwnersDirectory => Path.Combine(StateDirectory, "owners");

    private string StateDirectory { get; }

    public static string Fingerprint(string workingDirectory) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workingDirectory)))[..16];

    public static string Canonicalize(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var info = new DirectoryInfo(PlatformPath.Normalize(workingDirectory));
        var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
        return Path.TrimEndingDirectorySeparator(PlatformPath.Normalize((resolved ?? info).FullName));
    }

    public static OwnerRecord? Current(string ownerDirectory)
    {
        if (!Directory.Exists(ownerDirectory))
        {
            return null;
        }

        OwnerRecord? newest = null;
        var newestVersion = 0;
        foreach (var file in Directory.EnumerateFiles(ownerDirectory, "v*.json"))
        {
            var parsed = Read(file);
            if (parsed?.Version is int version && version > newestVersion)
            {
                newest = parsed;
                newestVersion = version;
            }
        }

        return newest;
    }

    public AdmissionResult DiscoverLatest(string workingDirectory)
    {
        var canonical = Canonicalize(workingDirectory);
        var associations = ReadAssociations(canonical, out var corrupt);
        if (corrupt)
        {
            return new AdmissionResult(ClaimDisposition.Corrupt, null, null, null);
        }

        var catalog = new SessionCatalog(new Parrot.State.StatePaths(StateDirectory, string.Empty, string.Empty));
        var candidates = new List<(UserSessionId Id, string Recency)>();
        foreach (var sessionId in associations)
        {
            _ = ReadActivation(sessionId, canonical, out corrupt);
            if (corrupt)
            {
                return new AdmissionResult(ClaimDisposition.Corrupt, null, null, null);
            }

            var metadata = catalog.Find(sessionId);
            var sessionDirectory = Path.Combine(StateDirectory, "sessions", sessionId.Value);
            if (metadata?.State == SessionCatalogState.Corrupt
                && (File.GetAttributes(sessionDirectory) & FileAttributes.ReparsePoint) == 0
                && !File.Exists(Path.Combine(sessionDirectory, "meta.json")))
            {
                metadata = null;
            }

            if (metadata is not null && (metadata.State == SessionCatalogState.Corrupt
                || !string.Equals(Canonicalize(metadata.WorkingDirectory), canonical, StringComparison.Ordinal)))
            {
                return new AdmissionResult(ClaimDisposition.Corrupt, null, null, null);
            }

            var recency = string.IsNullOrEmpty(metadata?.LastOpenedAt)
                ? metadata?.CreatedAt ?? string.Empty
                : metadata.LastOpenedAt;
            candidates.Add((sessionId, recency));
        }

        if (candidates.Count == 0)
        {
            return new AdmissionResult(ClaimDisposition.Fresh, null, null, null);
        }

        var selected = candidates.OrderByDescending(candidate => candidate.Recency, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Id.Value, StringComparer.Ordinal).First().Id;
        var status = ReadActivation(selected, canonical, out corrupt);
        var disposition = corrupt ? ClaimDisposition.Corrupt
            : status == ActivationStatus.Inactive ? ClaimDisposition.Resumed : ClaimDisposition.Live;
        return new AdmissionResult(
            disposition,
            selected,
            null,
            new OpenIntent(OpenOperation.Resume, selected, workingDirectory, canonical));
    }

    public AdmissionResult OpenDefault(string workingDirectory)
    {
        var candidate = DiscoverLatest(workingDirectory);
        return candidate.Disposition switch
        {
            ClaimDisposition.Fresh => CreateFresh(workingDirectory, UserSessionId.Generate()),
            ClaimDisposition.Resumed when candidate.SessionId is { } selected => Resume(workingDirectory, selected),
            _ => candidate,
        };
    }

    public AdmissionResult CreateFresh(string workingDirectory, UserSessionId sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        var canonical = Canonicalize(workingDirectory);
        var intent = new OpenIntent(OpenOperation.CreateFresh, sessionId, workingDirectory, canonical);
        var association = new OwnerRecord
        {
            SchemaVersion = SchemaVersion,
            UserSessionId = sessionId.Value,
            SessionId = sessionId.Value,
            WorkingDirectory = workingDirectory,
            CanonicalWorkspaceIdentity = canonical,
        };

        var associationDirectory = WorkspaceDirectory(canonical);
        _ = Directory.CreateDirectory(associationDirectory);
        var associationPath = Path.Combine(associationDirectory, $"{sessionId.Value}.json");
        if (!WriteOnce(associationPath, association))
        {
            var existing = Read(associationPath);
            if (!MatchesAssociation(existing, sessionId, canonical))
            {
                return new AdmissionResult(ClaimDisposition.Corrupt, sessionId, null, intent);
            }
        }

        return Activate(intent, ClaimDisposition.Fresh);
    }

    public AdmissionResult CreateFresh(string workingDirectory, string sessionId) =>
        CreateFresh(workingDirectory, UserSessionId.Parse(sessionId));

    public AdmissionResult Resume(string workingDirectory, UserSessionId sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        var canonical = Canonicalize(workingDirectory);
        var intent = new OpenIntent(OpenOperation.Resume, sessionId, workingDirectory, canonical);
        var associationPath = Path.Combine(WorkspaceDirectory(canonical), $"{sessionId.Value}.json");
        if (!File.Exists(associationPath))
        {
            var legacy = ReadLegacy(canonical, out var corrupt);
            if (corrupt || legacy is null || !TrySessionId(legacy, out var legacyId)
                || !legacyId.Equals(sessionId))
            {
                return new AdmissionResult(ClaimDisposition.Corrupt, sessionId, null, intent);
            }
        }
        else if (!MatchesAssociation(Read(associationPath), sessionId, canonical))
        {
            return new AdmissionResult(ClaimDisposition.Corrupt, sessionId, null, intent);
        }

        return Activate(intent, ClaimDisposition.Resumed);
    }

    public AdmissionResult Resume(string workingDirectory, string sessionId) =>
        Resume(workingDirectory, UserSessionId.Parse(sessionId));

    public ClaimResult Claim(string workingDirectory, string proposedSessionId, Func<int, bool> processIsAlive)
    {
        ArgumentNullException.ThrowIfNull(processIsAlive);
        var canonical = Canonicalize(workingDirectory);
        var directory = Path.Combine(OwnersDirectory, Fingerprint(canonical));
        _ = Directory.CreateDirectory(directory);
        var existing = Current(directory);
        if (existing?.ProcessId is int processId && processIsAlive(processId))
        {
            return new ClaimResult(existing.SessionId ?? proposedSessionId, ClaimDisposition.Live);
        }

        var resumed = existing?.SessionId ?? proposedSessionId;
        var version = (existing?.Version ?? 0) + 1;
        var record = new OwnerRecord
        {
            Version = version,
            SessionId = resumed,
            WorkingDirectory = workingDirectory,
            CanonicalWorkspaceIdentity = canonical,
            HostIdentity = _runtime.HostIdentity,
            ProcessId = _runtime.ProcessId,
            ProcessStartToken = _runtime.ProcessStartToken,
            BootIdentity = _runtime.BootIdentity,
            RuntimeInstanceId = _runtime.RuntimeInstanceId,
            LeaseId = Guid.NewGuid().ToString("n", System.Globalization.CultureInfo.InvariantCulture),
            Generation = version,
            State = ActivationState.Active.ToString(),
        };

        return WriteVersion(directory, record)
            ? new ClaimResult(resumed, existing is null ? ClaimDisposition.Fresh : ClaimDisposition.Reclaimed)
            : new ClaimResult(proposedSessionId, ClaimDisposition.Contended);
    }

    internal void Release(SessionActivationLease lease)
    {
        var directory = ActivationDirectory(lease.Intent.SessionId);
        var head = Current(directory);
        if (head is null || head.Generation != lease.Generation
            || !string.Equals(head.RuntimeInstanceId, lease.RuntimeInstanceId, StringComparison.Ordinal)
            || !string.Equals(head.LeaseId, lease.LeaseId, StringComparison.Ordinal)
            || !IsState(head, ActivationState.Active))
        {
            return;
        }

        var inactive = head with
        {
            Version = (head.Version ?? 0) + 1,
            State = ActivationState.Inactive.ToString(),
        };
        _ = WriteVersion(directory, inactive);
    }

    private static bool MatchesAssociation(OwnerRecord? record, UserSessionId sessionId, string canonical) =>
        record is not null && TrySessionId(record, out var recordedId) && recordedId.Equals(sessionId)
        && string.Equals(record.CanonicalWorkspaceIdentity, canonical, StringComparison.Ordinal);

    private static bool TrySessionId(
        OwnerRecord? record,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UserSessionId? sessionId) =>
        UserSessionId.TryParse(record?.UserSessionId ?? record?.SessionId, out sessionId);

    private static bool IsState(OwnerRecord record, ActivationState state) =>
        string.Equals(record.State, state.ToString(), StringComparison.OrdinalIgnoreCase);

    private static OwnerRecord? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), StoreJsonContext.Default.OwnerRecord);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool WriteVersion(string directory, OwnerRecord record)
    {
        var version = record.Version ?? throw new InvalidOperationException("A ledger record needs a version.");
        return WriteOnce(Path.Combine(directory, $"v{version}.json"), record);
    }

    private static bool WriteOnce(string target, OwnerRecord record)
    {
        var directory = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("The owner record path has no directory.");
        var staged = Path.Combine(directory, $".staging-{Guid.NewGuid():n}");
        File.WriteAllText(staged, JsonSerializer.Serialize(record, StoreJsonContext.Default.OwnerRecord));
        try
        {
            return Link(staged, target) == 0;
        }
        finally
        {
            File.Delete(staged);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Link(string existingPath, string newPath);

    private AdmissionResult Activate(OpenIntent intent, ClaimDisposition success)
    {
        var directory = ActivationDirectory(intent.SessionId);
        _ = Directory.CreateDirectory(directory);

        for (var attempt = 0; attempt < 32; attempt++)
        {
            var head = Current(directory);
            var status = DetermineStatus(head);
            if (status is ActivationStatus.Active or ActivationStatus.Uncertain)
            {
                return new AdmissionResult(ClaimDisposition.Live, intent.SessionId, null, intent);
            }

            var leaseId = Guid.NewGuid().ToString("n", System.Globalization.CultureInfo.InvariantCulture);
            var generation = (head?.Generation ?? 0) + 1;
            var record = new OwnerRecord
            {
                SchemaVersion = SchemaVersion,
                Version = (head?.Version ?? 0) + 1,
                Generation = generation,
                State = ActivationState.Active.ToString(),
                UserSessionId = intent.SessionId.Value,
                SessionId = intent.SessionId.Value,
                WorkingDirectory = intent.WorkingDirectory,
                CanonicalWorkspaceIdentity = intent.CanonicalWorkspaceIdentity,
                HostIdentity = _runtime.HostIdentity,
                BootIdentity = _runtime.BootIdentity,
                ProcessId = _runtime.ProcessId,
                ProcessStartToken = _runtime.ProcessStartToken,
                RuntimeInstanceId = _runtime.RuntimeInstanceId,
                LeaseId = leaseId,
            };
            if (!WriteVersion(directory, record))
            {
                continue;
            }

            return new AdmissionResult(
                success, intent, this, leaseId, generation, _runtime.RuntimeInstanceId);
        }

        return new AdmissionResult(ClaimDisposition.Contended, intent.SessionId, null, intent);
    }

    private List<UserSessionId> ReadAssociations(string canonical, out bool corrupt)
    {
        corrupt = false;
        var result = new Dictionary<string, UserSessionId>(StringComparer.Ordinal);
        var directory = WorkspaceDirectory(canonical);
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                var record = Read(file);
                if (!TrySessionId(record, out var sessionId)
                    || !MatchesAssociation(record, sessionId, canonical)
                    || !string.Equals(Path.GetFileNameWithoutExtension(file), sessionId.Value, StringComparison.Ordinal))
                {
                    corrupt = true;
                    return [];
                }

                result.Add(sessionId.Value, sessionId);
            }
        }

        var legacy = ReadLegacy(canonical, out var legacyCorrupt);
        if (legacyCorrupt)
        {
            corrupt = true;
            return [];
        }

        if (legacy is not null && TrySessionId(legacy, out var legacyId))
        {
            _ = result.TryAdd(legacyId.Value, legacyId);
        }

        return [.. result.Values];
    }

    private OwnerRecord? ReadLegacy(string canonical, out bool corrupt)
    {
        corrupt = false;
        var directory = Path.Combine(OwnersDirectory, Fingerprint(canonical));
        var record = Current(directory);
        if (record is null)
        {
            return null;
        }

        if (!TrySessionId(record, out _))
        {
            corrupt = true;
            return null;
        }

        string recordCanonical;
        try
        {
            recordCanonical = record.CanonicalWorkspaceIdentity
                ?? Canonicalize(record.WorkingDirectory ?? string.Empty);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            corrupt = true;
            return null;
        }

        if (!string.Equals(recordCanonical, canonical, StringComparison.Ordinal))
        {
            corrupt = true;
            return null;
        }

        return record;
    }

    private ActivationStatus ReadActivation(UserSessionId sessionId, string canonical, out bool corrupt)
    {
        corrupt = false;
        var head = Current(ActivationDirectory(sessionId));
        if (head is not null)
        {
            if (!TrySessionId(head, out var recordedId) || !recordedId.Equals(sessionId)
                || !string.Equals(head.CanonicalWorkspaceIdentity, canonical, StringComparison.Ordinal))
            {
                corrupt = true;
                return ActivationStatus.Uncertain;
            }

            return DetermineStatus(head);
        }

        var legacy = ReadLegacy(canonical, out corrupt);
        return legacy is not null && TrySessionId(legacy, out var legacyId) && legacyId.Equals(sessionId)
            ? DetermineStatus(legacy)
            : ActivationStatus.Inactive;
    }

    private ActivationStatus DetermineStatus(OwnerRecord? record)
    {
        if (record is null || IsState(record, ActivationState.Inactive))
        {
            return ActivationStatus.Inactive;
        }

        if (record.State is not null && !IsState(record, ActivationState.Active))
        {
            return ActivationStatus.Uncertain;
        }

        var hostMatches = record.HostIdentity is not null
            ? string.Equals(record.HostIdentity, _runtime.HostIdentity, StringComparison.Ordinal)
            : string.Equals(
                RuntimeIdentityCapture.FingerprintHost(record.HostKey ?? string.Empty),
                _runtime.HostIdentity,
                StringComparison.Ordinal);
        if (!hostMatches)
        {
            return ActivationStatus.Uncertain;
        }

        if (record.BootIdentity is not null
            && !string.Equals(record.BootIdentity, _runtime.BootIdentity, StringComparison.Ordinal))
        {
            return ActivationStatus.Uncertain;
        }

        if (record.ProcessId is not int processId)
        {
            return ActivationStatus.Uncertain;
        }

        var process = _runtime.Inspect(processId);
        if (process.Status == ProcessIdentityStatus.Unreadable)
        {
            return ActivationStatus.Uncertain;
        }

        if (process.Status == ProcessIdentityStatus.Missing)
        {
            return ActivationStatus.Inactive;
        }

        return record.ProcessStartToken is null
            || string.Equals(record.ProcessStartToken, process.ProcessStartToken, StringComparison.Ordinal)
                ? ActivationStatus.Active
                : ActivationStatus.Inactive;
    }

    private string WorkspaceDirectory(string canonical) =>
        Path.Combine(OwnersDirectory, "workspaces", Fingerprint(canonical));

    private string ActivationDirectory(UserSessionId sessionId) =>
        Path.Combine(OwnersDirectory, "activations", sessionId.Value);
}
