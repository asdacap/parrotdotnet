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
        var claim = new WorkingDirectoryClaim(_root, new RuntimeIdentityFixture("host", static _ => new ProcessIdentity(ProcessIdentityStatus.Missing, null), "start").Identity);
        var created = claim.CreateFresh(_workspace, "user-session-one");
        await ReleaseAsync(created.ActivationLease);

        var resumed = claim.OpenDefault(_workspace);

        _ = await Assert.That(resumed.Disposition).IsEqualTo(ClaimDisposition.Resumed);
        _ = await Assert.That(resumed.SessionId?.Value).IsEqualTo("user-session-one");
        await ReleaseAsync(resumed.ActivationLease);
    }

    [Test]
    public async Task Default_open_returns_live_candidate_without_activating()
    {
        var claim = new WorkingDirectoryClaim(_root, new RuntimeIdentityFixture("host", static _ => new ProcessIdentity(ProcessIdentityStatus.Found, "start"), "start").Identity);
        var first = claim.CreateFresh(_workspace, "user-session-one");

        var second = claim.OpenDefault(_workspace);

        _ = await Assert.That(first.Disposition).IsEqualTo(ClaimDisposition.Fresh);
        _ = await Assert.That(second.Disposition).IsEqualTo(ClaimDisposition.Live);
        _ = await Assert.That(second.SessionId?.Value).IsEqualTo("user-session-one");
        await ReleaseAsync(first.ActivationLease);
        await ReleaseAsync(second.ActivationLease);
    }

    [Test]
    public async Task Default_open_uses_ordinal_tie_break_for_multiple_inactive_associations()
    {
        var claim = new WorkingDirectoryClaim(_root, new RuntimeIdentityFixture("host", static _ => new ProcessIdentity(ProcessIdentityStatus.Missing, null), "start").Identity);
        var first = claim.CreateFresh(_workspace, "user-session-one");
        await ReleaseAsync(first.ActivationLease);
        var second = claim.CreateFresh(_workspace, "user-session-two");
        await ReleaseAsync(second.ActivationLease);

        var result = claim.OpenDefault(_workspace);

        _ = await Assert.That(result.Disposition).IsEqualTo(ClaimDisposition.Resumed);
        _ = await Assert.That(result.SessionId?.Value).IsEqualTo("user-session-one");
        await ReleaseAsync(result.ActivationLease);
    }

    [Test]
    public async Task Foreign_host_fails_closed_without_inspecting_its_process_id()
    {
        var foreign = new WorkingDirectoryClaim(_root, new RuntimeIdentityFixture("foreign", static _ => new ProcessIdentity(ProcessIdentityStatus.Found, "foreign-start"), "foreign-start").Identity);
        var foreignActivation = foreign.CreateFresh(_workspace, "user-session-foreign");
        var inspected = false;
        var localIdentity = new RuntimeIdentityFixture(
            "local",
            _ =>
            {
                inspected = true;
                return new ProcessIdentity(ProcessIdentityStatus.Missing, null);
            },
            "local-start").Identity;
        var local = new WorkingDirectoryClaim(_root, localIdentity);

        var result = local.OpenDefault(_workspace);

        _ = await Assert.That(inspected).IsFalse();
        _ = await Assert.That(result.Disposition).IsEqualTo(ClaimDisposition.Live);
        _ = await Assert.That(result.SessionId?.Value).IsEqualTo("user-session-foreign");
        await ReleaseAsync(foreignActivation.ActivationLease);
        await ReleaseAsync(result.ActivationLease);
    }

    [Test]
    public async Task Reused_pid_with_a_different_process_start_is_inactive()
    {
        var first = new WorkingDirectoryClaim(_root, new RuntimeIdentityFixture("host", static _ => new ProcessIdentity(ProcessIdentityStatus.Found, "old-start"), "old-start").Identity);
        var activation = first.CreateFresh(_workspace, "user-session-reused-pid");
        var second = new WorkingDirectoryClaim(_root, new RuntimeIdentityFixture("host", static _ => new ProcessIdentity(ProcessIdentityStatus.Found, "new-start"), "new-start").Identity);

        var result = second.OpenDefault(_workspace);

        _ = await Assert.That(result.Disposition).IsEqualTo(ClaimDisposition.Resumed);
        _ = await Assert.That(result.SessionId?.Value).IsEqualTo("user-session-reused-pid");
        await ReleaseAsync(activation.ActivationLease);
        await ReleaseAsync(result.ActivationLease);
    }

    [Test]
    [Arguments("unavailable", "unavailable")]
    [Arguments("unavailable-123", "unavailable")]
    [Arguments("unavailable-123", "unavailable-456")]
    [Arguments("unavailable--123", "unavailable")]
    [Arguments("boot", "unavailable")]
    [Arguments("unavailable", "boot")]
    [Arguments(null, "unavailable")]
    public async Task Unavailable_boot_identity_uses_process_liveness(string? recordedBoot, string currentBoot)
    {
        (ProcessIdentityStatus Status, string? Start, ClaimDisposition Expected)[] observations =
        [
            (ProcessIdentityStatus.Missing, null, ClaimDisposition.Resumed),
            (ProcessIdentityStatus.Found, "old-start", ClaimDisposition.Live),
            (ProcessIdentityStatus.Found, "new-start", ClaimDisposition.Resumed),
            (ProcessIdentityStatus.Unreadable, null, ClaimDisposition.Live),
            (ProcessIdentityStatus.Found, null, ClaimDisposition.Live),
            (ProcessIdentityStatus.Found, string.Empty, ClaimDisposition.Live),
            (ProcessIdentityStatus.Found, " ", ClaimDisposition.Live),
        ];
        foreach (var recordedStart in new[] { "old-start", null })
        {
            foreach (var (status, start, expectedDisposition) in observations)
            {
                var root = Path.Combine(_root, Guid.NewGuid().ToString("n"));
                var owner = new OwnerRecord
                {
                    Version = 1,
                    SessionId = "user-session-recovery",
                    WorkingDirectory = _workspace,
                    HostKey = "host",
                    BootIdentity = recordedBoot,
                    ProcessId = 4312,
                    ProcessStartToken = recordedStart,
                };
                var directory = Directory.CreateDirectory(Path.Combine(
                    root, "owners", WorkingDirectoryClaim.Fingerprint(WorkingDirectoryClaim.Canonicalize(_workspace))));
                await File.WriteAllTextAsync(
                    Path.Combine(directory.FullName, "v1.json"),
                    JsonSerializer.Serialize(owner, StoreJsonContext.Default.OwnerRecord));
                var inspected = false;
                var runtime = new RuntimeIdentity(
                    RuntimeIdentityCapture.FingerprintHost("host"),
                    currentBoot,
                    4312,
                    "new-start",
                    "runtime",
                    _ =>
                    {
                        inspected = true;
                        return new ProcessIdentity(status, start);
                    });
                var claim = new WorkingDirectoryClaim(root, runtime);

                var result = claim.OpenDefault(_workspace);

                var expected = recordedStart is null && status == ProcessIdentityStatus.Found
                    ? ClaimDisposition.Live : expectedDisposition;
                _ = await Assert.That(inspected).IsTrue();
                _ = await Assert.That(result.Disposition).IsEqualTo(expected);
                _ = await Assert.That(result.SessionId?.Value).IsEqualTo(owner.SessionId);
                _ = await Assert.That(result.ActivationLease is not null).IsEqualTo(expected == ClaimDisposition.Resumed);
                await ReleaseAsync(result.ActivationLease);
            }
        }
    }

    [Test]
    [Arguments("other-boot")]
    [Arguments("unavailable-")]
    [Arguments("unavailable-not-a-tick")]
    [Arguments("unavailable-123suffix")]
    [Arguments("unavailable-+123")]
    [Arguments("unavailable- 123")]
    [Arguments("unavailable-123 ")]
    [Arguments("unavailable-0123")]
    [Arguments("unavailable-9223372036854775808")]
    public async Task Available_boot_mismatch_fails_closed_without_inspecting_process(string recordedBoot)
    {
        var ownerRuntime = new RuntimeIdentity(
            RuntimeIdentityCapture.FingerprintHost("host"),
            recordedBoot,
            4312,
            "old-start",
            "owner-runtime",
            static _ => new ProcessIdentity(ProcessIdentityStatus.Found, "old-start"));
        var owner = new WorkingDirectoryClaim(_root, ownerRuntime);
        var activation = owner.CreateFresh(_workspace, "user-session-other-boot");
        var inspected = false;
        var currentRuntime = new RuntimeIdentity(
            RuntimeIdentityCapture.FingerprintHost("host"),
            "boot",
            4312,
            "new-start",
            "current-runtime",
            _ =>
            {
                inspected = true;
                return new ProcessIdentity(ProcessIdentityStatus.Missing, null);
            });
        var claim = new WorkingDirectoryClaim(_root, currentRuntime);

        var result = claim.OpenDefault(_workspace);

        _ = await Assert.That(inspected).IsFalse();
        _ = await Assert.That(result.Disposition).IsEqualTo(ClaimDisposition.Live);
        _ = await Assert.That(result.ActivationLease).IsNull();
        await ReleaseAsync(activation.ActivationLease);
    }

    [Test]
    public async Task Canonical_workspace_identity_matches_a_symbolic_link()
    {
        var alias = Path.Combine(_root, "workspace-alias");
        _ = Directory.CreateSymbolicLink(alias, _workspace);
        var claim = new WorkingDirectoryClaim(_root, new RuntimeIdentityFixture("host", static _ => new ProcessIdentity(ProcessIdentityStatus.Missing, null), "start").Identity);
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
        var claim = new WorkingDirectoryClaim(_root, new RuntimeIdentityFixture("host", static _ => new ProcessIdentity(ProcessIdentityStatus.Missing, null), "start").Identity);

        var result = claim.OpenDefault(_workspace);

        _ = await Assert.That(result.Disposition).IsEqualTo(ClaimDisposition.Resumed);
        _ = await Assert.That(result.SessionId?.Value).IsEqualTo(sessionId);
        await ReleaseAsync(result.ActivationLease);
    }

    [Test]
    public async Task Invalid_session_id_fails_before_filesystem_access()
    {
        var untouched = Path.Combine(_root, "untouched-state");
        var claim = new WorkingDirectoryClaim(untouched, new RuntimeIdentityFixture("host", static _ => new ProcessIdentity(ProcessIdentityStatus.Missing, null), "start").Identity);

        _ = await Assert.That(() => claim.Resume(_workspace, "../user-session-other")).Throws<FormatException>();
        _ = await Assert.That(Directory.Exists(untouched)).IsFalse();
    }

    [Test]
    public async Task Release_is_fenced_by_runtime_lease_and_generation()
    {
        var claim = new WorkingDirectoryClaim(_root, new RuntimeIdentityFixture("host", static _ => new ProcessIdentity(ProcessIdentityStatus.Found, "start"), "start").Identity);
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

    [Test]
    [Arguments(0, false, "")]
    [Arguments(1, false, "user-session-one")]
    [Arguments(2, false, "user-session-two")]
    [Arguments(2, true, "user-session-one")]
    public async Task Discovery_uses_successful_open_recency_without_activation(
        int count, bool reopened, string expected)
    {
        var claim = new WorkingDirectoryClaim(_root, new RuntimeIdentityFixture("host", static _ => new ProcessIdentity(ProcessIdentityStatus.Missing, null), "start").Identity);
        var paths = new Parrot.State.StatePaths(_root, string.Empty, string.Empty);
        var workspace = ProjectWorkspace.FromLaunchDirectory(_workspace);
        for (var index = 0; index < count; index++)
        {
            var id = UserSessionId.Parse(index == 0 ? "user-session-one" : "user-session-two");
            var admission = claim.CreateFresh(_workspace, id);
            await ReleaseAsync(admission.ActivationLease);
            new SessionIndex(new UserSessionResources(paths, id, workspace)).Publish(new SessionMeta
            {
                Id = id.Value,
                WorkingDirectory = _workspace,
                ProviderId = "unused",
                Model = "model",
                CreatedAt = index == 0 ? "2026-01-01T00:00:00Z" : "2026-02-01T00:00:00Z",
                LastOpenedAt = reopened && index == 0 ? "2026-03-01T00:00:00Z" : string.Empty,
            });
        }

        var otherWorkspace = Directory.CreateDirectory(Path.Combine(_root, "other-workspace")).FullName;
        var other = claim.CreateFresh(otherWorkspace, "user-session-other");
        await ReleaseAsync(other.ActivationLease);
        var result = claim.DiscoverLatest(_workspace);
        _ = await Assert.That(result.SessionId?.Value ?? string.Empty).IsEqualTo(expected);
        _ = await Assert.That(result.ActivationLease).IsNull();
        _ = await Assert.That(result.Disposition).IsEqualTo(count == 0 ? ClaimDisposition.Fresh : ClaimDisposition.Resumed);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Unreadable_owner_and_corrupt_metadata_are_not_activated(bool corrupt)
    {
        var claim = new WorkingDirectoryClaim(_root, new RuntimeIdentityFixture("host", static _ => new ProcessIdentity(ProcessIdentityStatus.Unreadable, null), "start").Identity);
        var admission = claim.CreateFresh(_workspace, "user-session-uncertain");
        if (corrupt)
        {
            var directory = Directory.CreateDirectory(Path.Combine(_root, "sessions", "user-session-uncertain"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "meta.json"), "not-json");
        }

        var result = claim.OpenDefault(_workspace);
        _ = await Assert.That(result.Disposition).IsEqualTo(corrupt ? ClaimDisposition.Corrupt : ClaimDisposition.Live);
        _ = await Assert.That(result.ActivationLease).IsNull();
        await ReleaseAsync(admission.ActivationLease);
    }

    [Test]
    public async Task Concurrent_resume_grants_exactly_one_activation()
    {
        var claim = new WorkingDirectoryClaim(_root, new RuntimeIdentityFixture("host", static _ => new ProcessIdentity(ProcessIdentityStatus.Found, "start"), "start").Identity);
        var initial = claim.CreateFresh(_workspace, "user-session-contended");
        await ReleaseAsync(initial.ActivationLease);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => claim.Resume(_workspace, "user-session-contended"))));
        _ = await Assert.That(attempts.Count(attempt => attempt.ActivationLease is not null)).IsEqualTo(1);
        _ = await Assert.That(attempts.Count(attempt => attempt.Disposition == ClaimDisposition.Live)).IsEqualTo(7);
        foreach (var attempt in attempts)
        {
            await ReleaseAsync(attempt.ActivationLease);
        }
    }

    private static async ValueTask ReleaseAsync(SessionActivationLease? lease)
    {
        if (lease is not null)
        {
            await lease.DisposeAsync();
        }
    }

    private sealed class RuntimeIdentityFixture(string hostKey, Func<int, ProcessIdentity> inspect, string processStartToken)
    {
        public RuntimeIdentity Identity { get; } = new(
            RuntimeIdentityCapture.FingerprintHost(hostKey),
            "boot",
            4312,
            processStartToken,
            Guid.NewGuid().ToString("n"),
            inspect);
    }
}
