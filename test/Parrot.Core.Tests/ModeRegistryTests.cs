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

        _ = await Assert.That(profile.PlanArtifact).IsEmpty();
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
        _ = await Assert.That(profile.PlanArtifact).IsEmpty();

        if (id == ModeRegistry.Plan)
        {
            profile.Prepare();
            _ = await Assert.That(profile.Prompt).Contains(profile.PlanArtifact);
            _ = await Assert.That(profile.Prompt).Contains(Path.Combine(_root, "plan"));
        }
    }

    [Test]
    public async Task Configured_profiles_compose_defaults_and_plan_keeps_a_directory_runtime_capability()
    {
        var denied = Path.Combine(_root, "denied");
        var allowed = Path.Combine(_root, "allowed");
        var configuration = Configuration.Load(
            Path.Combine(_root, "config.yaml"),
            Path.Combine(_root, "predefined_config.yaml"));
        var profiles = configuration.Profiles.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        profiles[ModeRegistry.Build] = configuration.Profiles[ModeRegistry.Build] with
        {
            ReadOnly = true,
            SandboxRules = [new SandboxRule(allowed, SandboxRuleAction.AllowWrite)],
        };
        var registry = new ModeRegistry(
            Path.Combine(_root, "plans"),
            [new SandboxRule(denied, SandboxRuleAction.DenyWrite)],
            profiles);

        var build = registry.Resolve(ModeRegistry.Build, "session");
        var plan = registry.Resolve(ModeRegistry.Plan, "session");

        _ = await Assert.That(build.ReadOnly).IsTrue();
        _ = await Assert.That(build.SecurityProfile.AllowsWrite(allowed)).IsTrue();
        _ = await Assert.That(build.SecurityProfile.AllowsWrite(denied)).IsFalse();
        var planDirectory = Path.Combine(_root, "plans");
        plan.Prepare();
        _ = await Assert.That(plan.SecurityProfile.AllowsWrite(plan.PlanArtifact)).IsTrue();
        _ = await Assert.That(plan.SecurityProfile.AllowsWrite(
            Path.Combine(planDirectory, "supporting.md"))).IsTrue();
        _ = await Assert.That(plan.SecurityProfile.AllowsWrite(
            Path.Combine(planDirectory, "..", "outside.md"))).IsFalse();
        _ = await Assert.That(plan.SecurityProfile.WithoutRuntimeCapabilities().AllowsWrite(plan.PlanArtifact)).IsFalse();
    }

    [Test]
    public async Task Plan_completion_trims_artifact_and_declares_approval_policy()
    {
        var profile = Registry().Resolve(ModeRegistry.Plan, "session");
        profile.Prepare();
        await File.WriteAllTextAsync(profile.PlanArtifact, "  # Plan\n\n- change code\n");

        var completed = profile.Complete("session", "message");

        if (completed is not { } emitted)
        {
            throw new InvalidOperationException("plan completion was not emitted");
        }

        _ = await Assert.That(emitted.SessionId).IsEqualTo("session");
        _ = await Assert.That(emitted.MessageId).IsEqualTo("message");
        _ = await Assert.That(emitted.Markdown).IsEqualTo("# Plan\n\n- change code");
        _ = await Assert.That(emitted.Dialog.Prompt).IsEqualTo("Plan complete: ");
        _ = await Assert.That(emitted.Dialog.Choices[0].Action.Mode).IsEqualTo(ModeRegistry.Build);
        _ = await Assert.That(emitted.Dialog.Choices[0].Action.Prompt).IsEqualTo("Implement the approved plan.");
        _ = await Assert.That(emitted.Dialog.EmptyMessage).IsEqualTo("enter yes, no, or feedback");
    }

    [Test]
    public async Task Plan_completion_uses_the_user_session_artifact_and_the_main_agent_identity()
    {
        var profile = Registry().Resolve(ModeRegistry.Plan, "user-session");
        profile.Prepare();
        await File.WriteAllTextAsync(profile.PlanArtifact, "# Plan");

        var completed = profile.Complete("main-agent-session", "message");

        if (completed is not { } emitted)
        {
            throw new InvalidOperationException("plan completion was not emitted");
        }

        _ = await Assert.That(emitted.SessionId).IsEqualTo("main-agent-session");
        _ = await Assert.That(emitted.Markdown).IsEqualTo("# Plan");
    }

    [Test]
    public async Task Plan_completion_omits_a_blank_artifact()
    {
        var profile = Registry().Resolve(ModeRegistry.Plan, "session");
        profile.Prepare();
        await File.WriteAllTextAsync(profile.PlanArtifact, " \n\t ");

        _ = await Assert.That(profile.Complete("session", "message")).IsNull();
    }

    [Test]
    public async Task Plan_artifacts_are_private_random_files_in_the_state_plan_directory()
    {
        var registry = Registry();
        var first = registry.Resolve(ModeRegistry.Plan, "first");
        var firstAgain = registry.Resolve(ModeRegistry.Plan, "first");
        var second = registry.Resolve(ModeRegistry.Plan, "second");

        _ = await Assert.That(first.PlanArtifact).IsEmpty();
        _ = await Assert.That(firstAgain.PlanArtifact).IsEmpty();
        _ = await Assert.That(second.PlanArtifact).IsEmpty();
        first.Prepare();
        firstAgain.Prepare();
        second.Prepare();
        _ = await Assert.That(first.PlanArtifact).IsEqualTo(firstAgain.PlanArtifact);
        _ = await Assert.That(first.PlanArtifact).IsNotEqualTo(second.PlanArtifact);
        _ = await Assert.That(Path.GetFileName(first.PlanArtifact)).StartsWith("plan-").And.EndsWith(".md");
        _ = await Assert.That(Path.GetDirectoryName(first.PlanArtifact)).IsEqualTo(Path.Combine(_root, "plan"));
    }

    private ModeRegistry Registry()
    {
        var configuration = Configuration.Load(
            Path.Combine(_root, "config.yaml"),
            Path.Combine(_root, "predefined_config.yaml"));
        return new ModeRegistry(Path.Combine(_root, "plan"), configuration.SandboxRules, configuration.Profiles);
    }
}
