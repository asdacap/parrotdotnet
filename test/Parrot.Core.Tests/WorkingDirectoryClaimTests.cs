using System.Text.Json;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class WorkingDirectoryClaimTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-admission-tests", Guid.NewGuid().ToString("n"));

    private readonly string _workspace;

    public WorkingDirectoryClaimTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        _ = Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Default_open_resumes_one_inactive_association()
    {
        var claim = Claim("host", static _ => Missing(), "start");
        var created = claim.CreateFresh(_workspace, "user-session-one");
        await ReleaseAsync(created.ActivationLease);

        var resumed = claim.OpenDefault(_workspace);

        _ = await Assert.That(resumed.Disposition).IsEqualTo(ClaimDisposition.Resumed);
        _ = await Assert.That(resumed.SessionId?.Value).IsEqualTo("user-session-one");
        await ReleaseAsync(resumed.ActivationLease);
    }

    [Test]
    public async Task Default_open_creates_fresh_when_all_associations_are_live()
    {
        var claim = Claim("host", static _ => Found("start"), "start");
        var first = claim.CreateFresh(_workspace, "user-session-one");

        var second = claim.OpenDefault(_workspace);

        _ = await Assert.That(first.Disposition).IsEqualTo(ClaimDisposition.Fresh);
        _ = await Assert.That(second.Disposition).IsEqualTo(ClaimDisposition.Fresh);
        _ = await Assert.That(second.SessionId?.Value).IsNotEqualTo("user-session-one");
        await ReleaseAsync(first.ActivationLease);
        await ReleaseAsync(second.ActivationLease);
    }

    [Test]
    public async Task Default_open_requires_selection_for_multiple_inactive_associations()
    {
        var claim = Claim("host", static _ => Missing(), "start");
        var first = claim.CreateFresh(_workspace, "user-session-one");
        await ReleaseAsync(first.ActivationLease);
        var second = claim.CreateFresh(_workspace, "user-session-two");
        await ReleaseAsync(second.ActivationLease);

        var result = claim.OpenDefault(_workspace);

        _ = await Assert.That(result.Disposition).IsEqualTo(ClaimDisposition.SelectionRequired);
        _ = await Assert.That(result.SessionId).IsNull();
        _ = await Assert.That(result.ActivationLease).IsNull();
    }

    [Test]
    public async Task Foreign_host_fails_closed_without_inspecting_its_process_id()
    {
        var foreign = Claim("foreign", static _ => Found("foreign-start"), "foreign-start");
        var foreignActivation = foreign.CreateFresh(_workspace, "user-session-foreign");
        var inspected = false;
        var local = Claim(
            "local",
            _ =>
            {
                inspected = true;
                return Missing();
            },
            "local-start");

        var result = local.OpenDefault(_workspace);

        _ = await Assert.That(inspected).IsFalse();
        _ = await Assert.That(result.Disposition).IsEqualTo(ClaimDisposition.Fresh);
        _ = await Assert.That(result.SessionId?.Value).IsNotEqualTo("user-session-foreign");
        await ReleaseAsync(foreignActivation.ActivationLease);
        await ReleaseAsync(result.ActivationLease);
    }

    [Test]
    public async Task Reused_pid_with_a_different_process_start_is_inactive()
    {
        var first = Claim("host", static _ => Found("old-start"), "old-start");
        var activation = first.CreateFresh(_workspace, "user-session-reused-pid");
        var second = Claim("host", static _ => Found("new-start"), "new-start");

        var result = second.OpenDefault(_workspace);

        _ = await Assert.That(result.Disposition).IsEqualTo(ClaimDisposition.Resumed);
        _ = await Assert.That(result.SessionId?.Value).IsEqualTo("user-session-reused-pid");
        await ReleaseAsync(activation.ActivationLease);
        await ReleaseAsync(result.ActivationLease);
    }

    [Test]
    public async Task Canonical_workspace_identity_matches_a_symbolic_link()
    {
        var alias = Path.Combine(_root, "workspace-alias");
        _ = Directory.CreateSymbolicLink(alias, _workspace);
        var claim = Claim("host", static _ => Missing(), "start");
        var created = claim.CreateFresh(alias, "user-session-linked");
        await ReleaseAsync(created.ActivationLease);

        var resumed = claim.OpenDefault(_workspace);

        _ = await Assert.That(resumed.Disposition).IsEqualTo(ClaimDisposition.Resumed);
        _ = await Assert.That(resumed.SessionId?.Value).IsEqualTo("user-session-linked");
        _ = await Assert.That(resumed.Intent?.WorkingDirectory).IsEqualTo(_workspace);
        await ReleaseAsync(resumed.ActivationLease);
    }

    [Test]
    public async Task Legacy_owner_preserves_the_exact_session_id_and_nullable_fields()
    {
        const string sessionId = "user-session-Legacy_exact.id";
        var canonical = WorkingDirectoryClaim.Canonicalize(_workspace);
        var directory = Path.Combine(_root, "owners", WorkingDirectoryClaim.Fingerprint(canonical));
        _ = Directory.CreateDirectory(directory);
        var legacy = new OwnerRecord
        {
            Version = 1,
            SessionId = sessionId,
            WorkingDirectory = _workspace,
            HostKey = "host",
            ProcessId = 4312,
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, "v1.json"),
            JsonSerializer.Serialize(legacy, StoreJsonContext.Default.OwnerRecord));
        var claim = Claim("host", static _ => Missing(), "start");

        var result = claim.OpenDefault(_workspace);

        _ = await Assert.That(result.Disposition).IsEqualTo(ClaimDisposition.Resumed);
        _ = await Assert.That(result.SessionId?.Value).IsEqualTo(sessionId);
        await ReleaseAsync(result.ActivationLease);
    }

    [Test]
    public async Task Invalid_session_id_fails_before_filesystem_access()
    {
        var untouched = Path.Combine(_root, "untouched-state");
        var claim = new WorkingDirectoryClaim(untouched, Identity("host", static _ => Missing(), "start"));

        _ = await Assert.That(() => claim.Resume(_workspace, "../user-session-other")).Throws<FormatException>();
        _ = await Assert.That(Directory.Exists(untouched)).IsFalse();
    }

    [Test]
    public async Task Release_is_fenced_by_runtime_lease_and_generation()
    {
        var claim = Claim("host", static _ => Found("start"), "start");
        var result = claim.CreateFresh(_workspace, "user-session-fenced");
        var lease = result.ActivationLease ?? throw new InvalidOperationException("Expected an activation lease.");
        var directory = Path.Combine(_root, "owners", "activations", "user-session-fenced");
        var current = WorkingDirectoryClaim.Current(directory)
            ?? throw new InvalidOperationException("Expected an activation head.");
        var replacement = current with
        {
            Version = 2,
            Generation = 2,
            RuntimeInstanceId = "replacement-runtime",
            LeaseId = "replacement-lease",
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, "v2.json"),
            JsonSerializer.Serialize(replacement, StoreJsonContext.Default.OwnerRecord));

        await lease.DisposeAsync();

        _ = await Assert.That(File.Exists(Path.Combine(directory, "v3.json"))).IsFalse();
        _ = await Assert.That(WorkingDirectoryClaim.Current(directory)?.RuntimeInstanceId)
            .IsEqualTo("replacement-runtime");
    }

    private static ProcessIdentity Missing() => new(ProcessIdentityStatus.Missing, null);

    private static ProcessIdentity Found(string processStartToken) =>
        new(ProcessIdentityStatus.Found, processStartToken);

    private static RuntimeIdentity Identity(
        string hostKey,
        Func<int, ProcessIdentity> inspect,
        string processStartToken) =>
        new(
            RuntimeIdentityCapture.FingerprintHost(hostKey),
            "boot",
            4312,
            processStartToken,
            Guid.NewGuid().ToString("n"),
            inspect);

    private static async ValueTask ReleaseAsync(SessionActivationLease? lease)
    {
        if (lease is not null)
        {
            await lease.DisposeAsync();
        }
    }

    private WorkingDirectoryClaim Claim(
        string hostKey,
        Func<int, ProcessIdentity> inspect,
        string processStartToken) =>
        new(_root, Identity(hostKey, inspect, processStartToken));
}
