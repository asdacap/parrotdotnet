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
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var environment = new ProcessEnvironmentOverrides(
        [
            new KeyValuePair<string, string>("COMMAND_VALUE", "present"),
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
        _ = await Assert.That(startInfo.ArgumentList).Contains("COMMAND_VALUE=present");
        _ = await Assert.That(startInfo.ArgumentList).DoesNotContain("TMPDIR=/command-temp");
        _ = await Assert.That(string.Join('|', startInfo.ArgumentList.TakeLast(3)))
            .IsEqualTo("/bin/sh|-c|printf test");
        _ = await Assert.That(startInfo.Environment.Count).IsEqualTo(1);
        _ = await Assert.That(startInfo.Environment["PATH"]).IsEqualTo("/usr/bin:/bin");
    }

    [Test]
    public async Task Writable_policy_limits_writes_and_preserves_protected_runtime_exceptions()
    {
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var denied = Directory.CreateDirectory(Path.Combine(_workspace, "denied")).FullName;
        var scratch = Scratch(resources);
        var profile = SecurityProfile.ForAgent(
            SecurityProfile.Compose(
                false,
                [new SandboxRule(denied, SandboxRuleAction.DenyWrite)],
                [],
                []),
            resources.Workspace.WritableRoots,
            resources.AgentsDirectory,
            []);

        var policy = MacSeatbeltSandbox.CompilePolicy(profile).Text;

        _ = await Assert.That(policy).Contains("(allow default)");
        _ = await Assert.That(policy).Contains("(deny file-write*");
        _ = await Assert.That(policy).Contains(Escape(_workspace));
        _ = await Assert.That(policy).Contains(Escape(denied));
        _ = await Assert.That(profile.AllowsWrite(scratch.Root)).IsTrue();
        _ = await Assert.That(policy).DoesNotContain("(allow file-write*");
    }

    [Test]
    public async Task Shared_scratch_omits_nested_write_denials_from_seatbelt_policy()
    {
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var siblingScratch = resources.AgentScratch(["sibling"]);
        var profile = SecurityProfile.ForAgent(
            SecurityProfile.Compose(
                false,
                [new SandboxRule(siblingScratch.Root, SandboxRuleAction.DenyWrite)],
                [],
                []),
            resources.Workspace.WritableRoots,
            resources.AgentsDirectory,
            []);

        var policy = MacSeatbeltSandbox.CompilePolicy(profile).Text;

        _ = await Assert.That(profile.AllowsWrite(Path.Combine(siblingScratch.Root, "file"))).IsTrue();
        _ = await Assert.That(policy).DoesNotContain(Escape(siblingScratch.Root));
    }

    [Test]
    public async Task Read_only_policy_ignores_workspace_writes_but_allows_shared_scratch()
    {
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var ignoredWorkspace = Directory.CreateTempSubdirectory();

        try
        {
            var profile = SecurityProfile.ForAgent(
                SecurityProfile.Compose(true, [], [], []),
                [ignoredWorkspace.FullName],
                resources.AgentsDirectory,
                []);

            var policy = MacSeatbeltSandbox.CompilePolicy(profile).Text;

            _ = await Assert.That(policy).Contains("(deny file-write*");
            _ = await Assert.That(policy).Contains(Escape(resources.AgentsDirectory));
            _ = await Assert.That(profile.AllowsWrite(
                Path.Combine(resources.AgentsDirectory, "agent-session-sibling", "file"))).IsTrue();
            _ = await Assert.That(policy).DoesNotContain(Escape(ignoredWorkspace.FullName));
        }
        finally
        {
            ignoredWorkspace.Delete(recursive: true);
        }
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

    private static AgentScratchDirectory Scratch(UserSessionResources resources) =>
        resources.AgentScratch(["main"]);
}
