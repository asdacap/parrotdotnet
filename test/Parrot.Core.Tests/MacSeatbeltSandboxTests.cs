using Parrot.Permissions;
using Parrot.Process;
using Parrot.Security;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class MacSeatbeltSandboxTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "parrot-seatbelt-tests", Guid.NewGuid().ToString("n"));

    public MacSeatbeltSandboxTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Test]
    public async Task Start_info_uses_a_profile_file_and_private_environment()
    {
        var resources = Resources();
        var environment = new ProcessEnvironmentOverrides(
        [
            new KeyValuePair<string, string>("COMMAND_VALUE", "present"),
            new KeyValuePair<string, string>("TMPDIR", "/command-temp"),
        ]);

        var startInfo = MacSeatbeltSandbox.CreateStartInfo(
            "/usr/bin/sandbox-exec",
            "/private/profile.sb",
            "printf test",
            environment,
            resources);

        _ = await Assert.That(startInfo.FileName).IsEqualTo("/usr/bin/sandbox-exec");
        _ = await Assert.That(startInfo.WorkingDirectory).IsEqualTo(_workspace);
        _ = await Assert.That(startInfo.RedirectStandardOutput).IsTrue();
        _ = await Assert.That(startInfo.RedirectStandardError).IsTrue();
        _ = await Assert.That(string.Join('|', startInfo.ArgumentList.Take(4)))
            .IsEqualTo("-f|/private/profile.sb|/usr/bin/env|-i");
        _ = await Assert.That(startInfo.ArgumentList).Contains($"HOME={resources.RuntimeHomeDirectory}");
        _ = await Assert.That(startInfo.ArgumentList).Contains($"XDG_CACHE_HOME={resources.CacheDirectory}");
        _ = await Assert.That(startInfo.ArgumentList).Contains("TMPDIR=/command-temp");
        _ = await Assert.That(startInfo.ArgumentList).Contains("COMMAND_VALUE=present");
        _ = await Assert.That(string.Join('|', startInfo.ArgumentList.TakeLast(3)))
            .IsEqualTo("/bin/sh|-c|printf test");
        _ = await Assert.That(startInfo.Environment.Count).IsEqualTo(1);
        _ = await Assert.That(startInfo.Environment["PATH"]).IsEqualTo("/usr/bin:/bin");
    }

    [Test]
    public async Task Writable_policy_limits_writes_and_preserves_protected_runtime_exceptions()
    {
        var resources = Resources();
        var outside = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n")));

        try
        {
            var grant = Directory.CreateDirectory(Path.Combine(outside.FullName, "grant")).FullName;
            var denied = Directory.CreateDirectory(Path.Combine(grant, "denied")).FullName;
            var grants = new SandboxWriteGrants();
            grants.Grant(SandboxWriteTarget.Resolve(grant));
            var profile = SecurityProfile.Compose(
                false,
                [new SandboxRule(denied, SandboxRuleAction.DenyWrite)],
                [],
                []);

            var policy = MacSeatbeltSandbox.CompilePolicy(resources, profile, grants.Capture()).Text;

            _ = await Assert.That(policy).Contains("(allow default)");
            _ = await Assert.That(policy).Contains("(deny file-write*");
            _ = await Assert.That(policy).Contains(Escape(_workspace));
            _ = await Assert.That(policy).Contains(Escape(grant));
            _ = await Assert.That(policy).Contains(Escape(denied));
            _ = await Assert.That(policy).Contains(Escape(resources.RuntimeHomeDirectory));
            _ = await Assert.That(policy).Contains(Escape(resources.BlobDirectory));
            _ = await Assert.That(policy).DoesNotContain("(allow file-write*");
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Read_only_policy_ignores_workspace_and_grant_writes()
    {
        var resources = Resources();
        var granted = Directory.CreateDirectory(Path.Combine(_workspace, "granted")).FullName;
        var grants = new SandboxWriteGrants();
        grants.Grant(SandboxWriteTarget.Resolve(granted));

        var policy = MacSeatbeltSandbox.CompilePolicy(
            resources,
            SecurityProfile.Compose(true, [], [], []),
            grants.Capture()).Text;

        _ = await Assert.That(policy).Contains("(deny file-write*");
        _ = await Assert.That(policy).Contains(Escape(resources.RuntimeHomeDirectory));
        _ = await Assert.That(policy).DoesNotContain(Escape(granted));
    }

    [Test]
    public async Task Profile_escapes_quotes_and_backslashes()
    {
        var policy = new SeatbeltPolicy();
        policy.AllowWrite(Path.Combine(_workspace, "quote\"and\\slash"));

        var text = policy.Capture().Text;

        _ = await Assert.That(text).Contains("quote\\\"and\\\\slash");
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private UserSessionResources Resources() =>
        new(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
}
