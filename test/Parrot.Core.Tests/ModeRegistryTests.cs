using Parrot.Agent;
using Parrot.Config;
using Parrot.Security;

namespace Parrot.Core.Tests;

internal sealed class ModeRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Registry_defaults_lists_and_rejects_unknown_modes()
    {
        var registry = Registry();

        _ = await Assert.That(registry.Resolve(string.Empty, "session").Id).IsEqualTo(ModeRegistry.Build);
        _ = await Assert.That(string.Join(" | ", registry.List())).IsEqualTo("build | plan | query");
        _ = await Assert.That(() => registry.Resolve("child", "session")).Throws<ModeRegistryException>();
    }

    [Test]
    public async Task Plan_prepare_creates_private_artifact_and_preserves_existing_content()
    {
        var profile = Registry().Resolve(ModeRegistry.Plan, "session");

        profile.Prepare();
        await File.WriteAllTextAsync(profile.PlanArtifact, "stale plan");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(profile.PlanArtifact, UnixFileMode.OtherRead | UnixFileMode.GroupRead);
        }

        profile.Prepare();

        _ = await Assert.That(File.Exists(profile.PlanArtifact)).IsTrue();
        _ = await Assert.That(await File.ReadAllTextAsync(profile.PlanArtifact)).IsEqualTo("stale plan");

        if (!OperatingSystem.IsWindows())
        {
            _ = await Assert.That(File.GetUnixFileMode(profile.PlanArtifact))
                .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Test]
    [Arguments(ModeRegistry.Build, false, 64, "build mode", "authorized workspace")]
    [Arguments(ModeRegistry.Plan, true, 24, "plan mode", "only writable location")]
    [Arguments(ModeRegistry.Query, true, 24, "query mode", "Read-only mode")]
    public async Task Foreground_modes_expose_their_policy(
        string id,
        bool readOnly,
        int maxToolRounds,
        string promptFragment,
        string ruleFragment)
    {
        var profile = Registry().Resolve(id, "session");

        _ = await Assert.That(profile.Id).IsEqualTo(id);
        _ = await Assert.That(profile.ReadOnly).IsEqualTo(readOnly);
        _ = await Assert.That(profile.MaxToolRounds).IsEqualTo(maxToolRounds);
        _ = await Assert.That(profile.Prompt).Contains(promptFragment);
        _ = await Assert.That(profile.HardRule).Contains(ruleFragment);
        _ = await Assert.That(profile.Status).IsNotEmpty();
        _ = await Assert.That(profile.PlanArtifact.Length > 0).IsEqualTo(id == ModeRegistry.Plan);
    }

    [Test]
    public async Task Configured_profiles_compose_defaults_and_plan_keeps_a_directory_runtime_capability()
    {
        var denied = Path.Combine(_root, "denied");
        var allowed = Path.Combine(_root, "allowed");
        var registry = new ModeRegistry(
            Path.Combine(_root, "plans"),
            [new SandboxRule(denied, SandboxRuleAction.DenyWrite)],
            new Dictionary<string, ProfileSecurityConfig>(StringComparer.Ordinal)
            {
                [ModeRegistry.Build] = new()
                {
                    ReadOnly = true,
                    SandboxRules = [new SandboxRule(allowed, SandboxRuleAction.AllowWrite)],
                },
            });

        var build = registry.Resolve(ModeRegistry.Build, "session");
        var plan = registry.Resolve(ModeRegistry.Plan, "session");

        _ = await Assert.That(build.ReadOnly).IsTrue();
        _ = await Assert.That(build.SecurityProfile.AllowsWrite(allowed)).IsTrue();
        _ = await Assert.That(build.SecurityProfile.AllowsWrite(denied)).IsFalse();
        var planDirectory = Path.Combine(_root, "plans");
        _ = await Assert.That(plan.SecurityProfile.AllowsWrite(plan.PlanArtifact)).IsTrue();
        _ = await Assert.That(plan.SecurityProfile.AllowsWrite(
            Path.Combine(planDirectory, "supporting.md"))).IsTrue();
        _ = await Assert.That(plan.SecurityProfile.AllowsWrite(
            Path.Combine(planDirectory, "..", "outside.md"))).IsFalse();
        _ = await Assert.That(plan.SecurityProfile.WithoutRuntimeCapabilities().AllowsWrite(plan.PlanArtifact)).IsFalse();
    }

    [Test]
    public async Task Plan_artifacts_are_private_random_files_in_the_state_plan_directory()
    {
        var registry = Registry();
        var first = registry.Resolve(ModeRegistry.Plan, "first");
        var firstAgain = registry.Resolve(ModeRegistry.Plan, "first");
        var second = registry.Resolve(ModeRegistry.Plan, "second");

        _ = await Assert.That(first.PlanArtifact).IsEqualTo(firstAgain.PlanArtifact);
        _ = await Assert.That(first.PlanArtifact).IsNotEqualTo(second.PlanArtifact);
        _ = await Assert.That(Path.GetFileName(first.PlanArtifact)).StartsWith("plan-").And.EndsWith(".md");
        _ = await Assert.That(Path.GetDirectoryName(first.PlanArtifact)).IsEqualTo(Path.Combine(_root, "plan"));
    }

    private ModeRegistry Registry() => new(Path.Combine(_root, "plan"));
}
