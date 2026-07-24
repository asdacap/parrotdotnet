using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Parrot.Store;

// Binds a working directory to a user session, per host.
//
// A claim is made with link() onto a version-named target, which reports EEXIST
// rather than overwriting. rename would silently discard a competing claim, and
// no lock manager is involved: a working directory is a host-local name, so the
// binding is host-local too. Owner records are never read to decide another
// host's claim.
internal sealed partial class WorkingDirectoryClaim(string stateDirectory, string hostKey)
{
    public string OwnersDirectory => Path.Combine(stateDirectory, "owners");

    public static string Fingerprint(string workingDirectory) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(workingDirectory)))[..16];

    public static OwnerRecord? Current(string ownerDirectory)
    {
        if (!Directory.Exists(ownerDirectory))
        {
            return null;
        }

        OwnerRecord? newest = null;

        foreach (var file in Directory.EnumerateFiles(ownerDirectory, "v*.json"))
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize(
                File.ReadAllText(file), StoreJsonContext.Default.OwnerRecord);

            if (parsed is not null && (newest is null || parsed.Version > newest.Version))
            {
                newest = parsed;
            }
        }

        return newest;
    }

    // Returns the session id now bound to this directory. If a live claim
    // exists it is returned unchanged, which is how a second parrot in the same
    // directory ends up with its own session rather than stealing one.
    public ClaimResult Claim(string workingDirectory, string proposedSessionId, Func<int, bool> processIsAlive)
    {
        ArgumentNullException.ThrowIfNull(processIsAlive);

        var directory = Path.Combine(OwnersDirectory, Fingerprint(workingDirectory));
        _ = Directory.CreateDirectory(directory);

        var existing = Current(directory);

        if (existing is not null && processIsAlive(existing.ProcessId))
        {
            return new ClaimResult(existing.SessionId, ClaimDisposition.Live);
        }

        // Reclaiming an abandoned binding resumes the session it was bound to.
        // Minting a new id here would silently start a fresh conversation every
        // time a process died, which is the opposite of surviving a crash.
        var resumed = existing?.SessionId ?? proposedSessionId;
        var version = (existing?.Version ?? 0) + 1;
        var record = new OwnerRecord
        {
            Version = version,
            SessionId = resumed,
            WorkingDirectory = workingDirectory,
            HostKey = hostKey,
            ProcessId = Environment.ProcessId,
        };

        // A failed write means somebody claimed the same version first: link()
        // said EEXIST, that claim stands, and this process takes its own
        // session rather than overwriting.
        if (!Write(directory, record))
        {
            return new ClaimResult(proposedSessionId, ClaimDisposition.Contended);
        }

        return new ClaimResult(
            resumed, existing is null ? ClaimDisposition.Fresh : ClaimDisposition.Reclaimed);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Link(string existingPath, string newPath);

    // link() onto the version-named target, never rename: rename would replace
    // whatever a concurrent claimer had just put there.
    private static bool Write(string directory, OwnerRecord record)
    {
        var staged = Path.Combine(directory, $"v{record.Version}.staging-{Environment.ProcessId}");
        var target = Path.Combine(directory, $"v{record.Version}.json");

        File.WriteAllText(
            staged, System.Text.Json.JsonSerializer.Serialize(record, StoreJsonContext.Default.OwnerRecord));

        try
        {
            // A hard link, not a symlink and not O_EXCL. link() reports EEXIST
            // rather than overwriting, and unlike O_EXCL it is atomic on NFS --
            // which is the whole reason the storage layout uses it.
            return Link(staged, target) == 0;
        }
        finally
        {
            File.Delete(staged);
        }
    }
}
