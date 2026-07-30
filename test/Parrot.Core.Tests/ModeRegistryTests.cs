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

        _ = await Assert.That(registry.Resolve(string.Empty).Id).IsEqualTo(ModeRegistry.Build);
        _ = await Assert.That(string.Join(" | ", registry.List())).IsEqualTo("build | plan | query");
        _ = await Assert.That(() => registry.Resolve("child")).Throws<ModeRegistryException>();
    }

    [Test]
    public async Task Profile_registry_partitions_profiles_resolves_the_explore_alias_and_copies_collections()
    {
        var configuration = Configuration.Load(
            Path.Combine(_root, "config.yaml"),
            Path.Combine(_root, "predefined_config.yaml"));
        var profiles = new ProfileRegistry(
            configuration.Profiles,
            configuration.SandboxRules,
            configuration.DisabledTools,
            configuration.DefaultProfile);

        _ = await Assert.That(string.Join(" | ", profiles.Foreground.Select(profile => profile.Id)))
            .IsEqualTo("build | plan | query");
        _ = await Assert.That(string.Join(" | ", profiles.Children.Select(profile => profile.Id)))
            .IsEqualTo("explorer | review | thinker | worker");
        _ = await Assert.That(profiles.ResolveChild("explore").Id).IsEqualTo("explorer");
        _ = await Assert.That(() => profiles.ResolveChild("build")).Throws<AgentRegistryException>();
        _ = await Assert.That(() => profiles.ResolveForeground("worker")).Throws<ModeRegistryException>();

        var profile = profiles.ResolveChild("thinker");
        var tools = profile.AllowedTools
            ?? throw new InvalidOperationException("thinker must define its tool allowlist");

        _ = await Assert.That(tools[0]).IsEqualTo("agent_spawn");
    }

    [Test]
    public async Task Plan_prepare_creates_private_artifact_and_preserves_existing_content()
    {
        var profile = OwnerModes("session").Resolve(ModeRegistry.Plan);

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
    [Arguments(ModeRegistry.Build, false, 1024, "build mode", "authorized workspace")]
    [Arguments(ModeRegistry.Plan, true, 1024, "plan mode", "designated plan-artifact directory")]
    [Arguments(ModeRegistry.Query, true, 8, "query mode", "Read-only mode")]
    public async Task Foreground_modes_expose_their_policy(
        string id,
        bool readOnly,
        int maxToolRounds,
        string promptFragment,
        string ruleFragment)
    {
        var profile = OwnerModes("session").Resolve(id);

        _ = await Assert.That(profile.Id).IsEqualTo(id);
        _ = await Assert.That(profile.ReadOnly).IsEqualTo(readOnly);
        _ = await Assert.That(profile.MaxTurns).IsEqualTo(maxToolRounds);
        _ = await Assert.That(profile.Prompt).Contains(promptFragment);
        _ = await Assert.That(profile.HardRules[0]).Contains(ruleFragment);
        _ = await Assert.That(profile.PlanArtifact).IsEmpty();

        if (id == ModeRegistry.Plan)
        {
            profile.Prepare();
            _ = await Assert.That(profile.Prompt).Contains(profile.PlanArtifact);
            _ = await Assert.That(profile.Prompt).Contains(Path.Combine(_root, "sessions", "session", "plan"));
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
            new ProfileRegistry(
                profiles,
                [new SandboxRule(denied, SandboxRuleAction.DenyWrite)],
                configuration.DisabledTools,
                ModeRegistry.Build));

        var ownerModes = new UserSessionModes(registry, Path.Combine(_root, "plans"));
        var build = ownerModes.Resolve(ModeRegistry.Build);
        var plan = ownerModes.Resolve(ModeRegistry.Plan);

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
        var profile = OwnerModes("session").Resolve(ModeRegistry.Plan);
        profile.Prepare();
        await File.WriteAllTextAsync(profile.PlanArtifact, "  # Plan\n\n- change code\n");

        var completed = profile.Complete("session", "message");

        if (completed is not { } emitted)
        {
            throw new InvalidOperationException("plan completion was not emitted");
        }

        _ = await Assert.That(emitted.AgentSessionId).IsEqualTo("session");
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
        var profile = OwnerModes("user-session").Resolve(ModeRegistry.Plan);
        profile.Prepare();
        await File.WriteAllTextAsync(profile.PlanArtifact, "# Plan");

        var completed = profile.Complete("main-agent-session", "message");

        if (completed is not { } emitted)
        {
            throw new InvalidOperationException("plan completion was not emitted");
        }

        _ = await Assert.That(emitted.AgentSessionId).IsEqualTo("main-agent-session");
        _ = await Assert.That(emitted.Markdown).IsEqualTo("# Plan");
    }

    [Test]
    public async Task Plan_completion_omits_a_blank_artifact()
    {
        var profile = OwnerModes("session").Resolve(ModeRegistry.Plan);
        profile.Prepare();
        await File.WriteAllTextAsync(profile.PlanArtifact, " \n\t ");

        _ = await Assert.That(profile.Complete("session", "message")).IsNull();
    }

    [Test]
    public async Task Fresh_owner_state_does_not_revive_an_artifact_from_an_earlier_attempt()
    {
        var planDirectory = Path.Combine(_root, "sessions", "same-owner", "plan");
        var previous = new UserSessionModes(Registry(), planDirectory).Resolve(ModeRegistry.Plan);
        previous.Prepare();
        await File.WriteAllTextAsync(previous.PlanArtifact, "stale plan");

        var current = new UserSessionModes(Registry(), planDirectory).Resolve(ModeRegistry.Plan);
        current.Prepare();

        _ = await Assert.That(current.PlanArtifact).IsNotEqualTo(previous.PlanArtifact);
        _ = await Assert.That(await File.ReadAllTextAsync(current.PlanArtifact)).IsEmpty();
    }

    [Test]
    public async Task Plan_artifacts_are_private_random_files_in_the_owner_plan_directory()
    {
        var registry = Registry();
        var firstModes = new UserSessionModes(registry, Path.Combine(_root, "sessions", "first", "plan"));
        var secondModes = new UserSessionModes(registry, Path.Combine(_root, "sessions", "second", "plan"));
        var first = firstModes.Resolve(ModeRegistry.Plan);
        var firstAgain = firstModes.Resolve(ModeRegistry.Plan);
        var second = secondModes.Resolve(ModeRegistry.Plan);

        _ = await Assert.That(first.PlanArtifact).IsEmpty();
        _ = await Assert.That(firstAgain.PlanArtifact).IsEmpty();
        _ = await Assert.That(second.PlanArtifact).IsEmpty();
        first.Prepare();
        firstAgain.Prepare();
        second.Prepare();
        _ = await Assert.That(first.PlanArtifact).IsEqualTo(firstAgain.PlanArtifact);
        _ = await Assert.That(first.PlanArtifact).IsNotEqualTo(second.PlanArtifact);
        _ = await Assert.That(Path.GetFileName(first.PlanArtifact)).StartsWith("plan-").And.EndsWith(".md");
        _ = await Assert.That(Path.GetDirectoryName(first.PlanArtifact)).IsEqualTo(Path.Combine(_root, "sessions", "first", "plan"));
    }

    private UserSessionModes OwnerModes(string ownerId) =>
        new(Registry(), Path.Combine(_root, "sessions", ownerId, "plan"));

    private ModeRegistry Registry()
    {
        var configuration = Configuration.Load(
            Path.Combine(_root, "config.yaml"),
            Path.Combine(_root, "predefined_config.yaml"));
        return new ModeRegistry(
            new ProfileRegistry(
                configuration.Profiles,
                configuration.SandboxRules,
                configuration.DisabledTools,
                configuration.DefaultProfile));
    }
}
