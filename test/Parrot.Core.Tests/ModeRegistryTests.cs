using System.Text.Json;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.State;
using Parrot.Store;
using Parrot.Tools;

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
            [],
            configuration.DisabledTools);
        var modes = new ModeRegistry(profiles, configuration.DefaultProfile);

        _ = await Assert.That(string.Join(" | ", modes.List())).IsEqualTo("build | plan | query");
        _ = await Assert.That(string.Join(" | ", profiles.Children.Select(profile => profile.Id)))
            .IsEqualTo("agent-task-payload | agent-task-pre-hook | agent-task-validation | explorer | review | thinker | worker");
        _ = await Assert.That(profiles.ResolveChild("explore").Id).IsEqualTo("explorer");
        var worker = profiles.ResolveChild("worker");
        _ = await Assert.That(worker.Id).IsEqualTo("worker");
        _ = await Assert.That(() => profiles.ResolveChild("build")).Throws<AgentRegistryException>();
        _ = await Assert.That(() => modes.Resolve("worker")).Throws<ModeRegistryException>();

        var profile = profiles.ResolveChild("thinker");
        var tools = profile.AllowedTools
            ?? throw new InvalidOperationException("thinker must define its tool allowlist");

        _ = await Assert.That(tools[0]).IsEqualTo("agent_spawn");
    }

    [Test]
    public async Task Independent_selectability_flags_partition_modes_and_children()
    {
        var configuration = Configuration.Load(
            Path.Combine(_root, "config.yaml"),
            Path.Combine(_root, "predefined_config.yaml"));
        var profiles = configuration.Profiles.ToDictionary(
            entry => entry.Key,
            entry => entry.Value,
            StringComparer.Ordinal);
        profiles[ModeRegistry.Build] = profiles[ModeRegistry.Build] with { IsAgentSelectable = true };
        profiles["worker"] = profiles["worker"] with { IsUserSelectable = true };
        profiles["review"] = profiles["review"] with { IsUserSelectable = false, IsAgentSelectable = false };
        var registry = new ProfileRegistry(profiles, configuration.SandboxRules, [], configuration.DisabledTools);
        var modes = new ModeRegistry(registry, ModeRegistry.Build);

        _ = await Assert.That(string.Join(" | ", modes.List())).IsEqualTo("build | plan | query | worker");
        _ = await Assert.That(string.Join(" | ", registry.Children.Select(profile => profile.Id)))
            .IsEqualTo("agent-task-payload | agent-task-pre-hook | agent-task-validation | build | explorer | thinker | worker");
        _ = await Assert.That(modes.Resolve("worker").Id).IsEqualTo("worker");
        _ = await Assert.That(registry.ResolveChild(ModeRegistry.Build).Id).IsEqualTo(ModeRegistry.Build);
        _ = await Assert.That(() => modes.Resolve("review")).Throws<ModeRegistryException>();
        _ = await Assert.That(() => registry.ResolveChild("review")).Throws<AgentRegistryException>();
    }

    [Test]
    public async Task Plan_prepare_creates_private_artifact_and_preserves_existing_content()
    {
        var profile = OwnerModes("session").Resolve(ModeRegistry.Plan);
        profile.Prepare();
        var artifact = PlanArtifact("session");
        await File.WriteAllTextAsync(artifact, "stale plan");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(artifact, UnixFileMode.OtherRead | UnixFileMode.GroupRead);
        }

        profile.Prepare();

        _ = await Assert.That(File.Exists(artifact)).IsTrue();
        _ = await Assert.That(await File.ReadAllTextAsync(artifact)).IsEqualTo("stale plan");

        if (!OperatingSystem.IsWindows())
        {
            _ = await Assert.That(File.GetUnixFileMode(artifact))
                .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Test]
    [Arguments(ModeRegistry.Build, false, 1024, "build mode", "authorized workspace")]
    [Arguments(ModeRegistry.Plan, true, 1024, "plan mode", "provided plan directory")]
    [Arguments(ModeRegistry.Query, true, 8, "query mode", "Read-only mode")]
    public async Task Foreground_modes_expose_their_policy(
        string id,
        bool readOnly,
        int maxToolRounds,
        string promptFragment,
        string policyFragment)
    {
        var profile = OwnerModes("session").Resolve(id);

        _ = await Assert.That(profile.Id).IsEqualTo(id);
        _ = await Assert.That(profile.SecurityProfile.ReadOnly).IsEqualTo(readOnly);
        _ = await Assert.That(profile.MaxTurns).IsEqualTo(maxToolRounds);
        _ = await Assert.That(profile.EnforceActiveWorkCompletion).IsTrue();
        _ = await Assert.That(profile.Prompt).Contains(promptFragment);
        _ = await Assert.That(profile.Prompt).Contains(policyFragment);

        if (id == ModeRegistry.Plan)
        {
            profile.Prepare();
            var artifact = PlanArtifact("session");
            _ = await Assert.That(profile.Prompt).Contains(artifact);
            _ = await Assert.That(profile.Prompt).Contains(Path.Combine(_root, "sessions", "session", "plan"));
        }
    }

    [Test]
    public async Task Default_cache_rule_grants_all_profiles()
    {
        var cache = Path.Combine(_root, "cache");
        var configuration = Configuration.Load(
            Path.Combine(_root, "config.yaml"),
            Path.Combine(_root, "predefined_config.yaml"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = Path.Combine(_root, "home"),
                ["XDG_CACHE_HOME"] = cache,
            });
        var modes = new ModeRegistry(
            new ProfileRegistry(
                configuration.Profiles,
                configuration.SandboxRules,
                [],
                configuration.DisabledTools),
            configuration.DefaultProfile);

        _ = await Assert.That(modes.Resolve(ModeRegistry.Build).SecurityProfile.AllowsWrite(cache)).IsTrue();
        _ = await Assert.That(modes.Resolve(ModeRegistry.Plan).SecurityProfile.AllowsWrite(cache)).IsTrue();
        _ = await Assert.That(modes.Resolve(ModeRegistry.Query).SecurityProfile.AllowsWrite(cache)).IsTrue();
    }

    [Test]
    public async Task Configured_profiles_compose_defaults_and_raw_plan_policy_denies_artifacts()
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
                [],
                configuration.DisabledTools),
            ModeRegistry.Build);

        var planDirectory = Path.Combine(_root, "plans", "plan");
        var ownerModes = new UserSessionModes(registry, TestModels.PromptTemplates, planDirectory);
        var build = ownerModes.Resolve(ModeRegistry.Build);
        var plan = ownerModes.Resolve(ModeRegistry.Plan);

        _ = await Assert.That(build.SecurityProfile.ReadOnly).IsTrue();
        _ = await Assert.That(build.SecurityProfile.AllowsWrite(allowed)).IsTrue();
        _ = await Assert.That(build.SecurityProfile.AllowsWrite(denied)).IsFalse();
        plan.Prepare();
        var artifact = PlanArtifactIn(planDirectory);
        _ = await Assert.That(plan.SecurityProfile.AllowsWrite(artifact)).IsFalse();
        _ = await Assert.That(plan.SecurityProfile.AllowsWrite(
            Path.Combine(planDirectory, "supporting.md"))).IsFalse();
        _ = await Assert.That(plan.SecurityProfile.AllowsWrite(
            Path.Combine(planDirectory, "..", "outside.md"))).IsFalse();
    }

    [Test]
    public async Task Runtime_scratch_grant_confines_WriteTool_to_the_user_session_scratch_root(
        CancellationToken cancellationToken)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var paths = new StatePaths(
            Path.Combine(_root, "state"),
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"));
        var registry = Registry([new SandboxRule(_root, SandboxRuleAction.DenyWrite)]);
        var planDirectory = Path.Combine(paths.State, "sessions", "session", "plan");
        var plan = new UserSessionModes(registry, TestModels.PromptTemplates, planDirectory).Resolve(ModeRegistry.Plan);
        plan.Prepare();
        var artifact = PlanArtifactIn(planDirectory);
        var outside = Path.Combine(_root, "outside.md");
        var scratch = new AgentScratchDirectory(Path.GetDirectoryName(planDirectory)
            ?? throw new InvalidOperationException("Plan directory has no parent."));
        var security = SecurityProfile.ForAgent(plan.SecurityProfile, [], scratch.Root, []);
        var write = new WriteTool(new ToolWorkspace(workspace));

        var supporting = Path.Combine(planDirectory, "supporting.md");
        var written = await write.Execute(
            new ToolInvocation("write-plan", WriteArguments(artifact, "# Plan")),
            Turn(security),
            cancellationToken);
        var supported = await write.Execute(
            new ToolInvocation("write-supporting", WriteArguments(supporting, "details")),
            Turn(security),
            cancellationToken);
        var denied = await write.Execute(
            new ToolInvocation("write-outside", WriteArguments(outside, "outside")),
            Turn(security),
            cancellationToken);

        _ = await Assert.That(written.Text).DoesNotStartWith("error: ");
        _ = await Assert.That(supported.Text).DoesNotStartWith("error: ");
        _ = await Assert.That(await File.ReadAllTextAsync(artifact, cancellationToken)).IsEqualTo("# Plan");
        _ = await Assert.That(await File.ReadAllTextAsync(supporting, cancellationToken)).IsEqualTo("details");
        _ = await Assert.That(denied.Text).StartsWith("error: ");
        _ = await Assert.That(File.Exists(outside)).IsFalse();
    }

    [Test]
    public async Task Plan_completion_trims_artifact_and_declares_approval_policy()
    {
        var profile = OwnerModes("session", Registry([], ModeRegistry.Query)).Resolve(ModeRegistry.Plan);
        profile.Prepare();
        var artifact = PlanArtifact("session");
        await File.WriteAllTextAsync(artifact, "  # Plan\n\n- change code\n");
        await WriteValidTasks(artifact);

        var completed = profile.Complete("session", "message").Completion;

        if (completed is not { } emitted)
        {
            throw new InvalidOperationException("plan completion was not emitted");
        }

        _ = await Assert.That(emitted.AgentSessionId).IsEqualTo("session");
        _ = await Assert.That(emitted.MessageId).IsEqualTo("message");
        _ = await Assert.That(emitted.Markdown).IsEqualTo("# Plan\n\n- change code");
        _ = await Assert.That(emitted.Dialog.Prompt).IsEqualTo("Plan complete: ");
        _ = await Assert.That(emitted.Dialog.Choices[0].Action.Mode).IsEqualTo(ModeRegistry.Build);
        _ = await Assert.That(emitted.Dialog.Choices[0].Action.Prompt)
            .IsEqualTo($"Implement the approved Markdown plan at {artifact}. Call run_agent_tasks with the approved task JSON path {TaskArtifactFor(artifact)}.");
        _ = await Assert.That(emitted.Dialog.EmptyMessage).IsEqualTo("enter yes, no, or feedback");
    }

    [Test]
    public async Task Plan_completion_projects_validated_tasks_as_an_ordered_pending_tree()
    {
        var profile = OwnerModes("tree").Resolve(ModeRegistry.Plan);
        profile.Prepare();
        var artifact = PlanArtifact("tree");
        await File.WriteAllTextAsync(artifact, "# Plan");
        await File.WriteAllTextAsync(
            TaskArtifactFor(artifact),
            "{\"schema_version\":1,\"tasks\":[{\"name\":\"first\",\"description\":\"First\",\"payload\":[{\"name\":\"child-first\",\"description\":\"Child first\",\"payload\":\"Do it\",\"acceptance_criteria\":\"Pass\"},{\"name\":\"child-second\",\"description\":\"Child second\",\"payload\":[{\"name\":\"grandchild\",\"description\":\"Grandchild\",\"payload\":\"Do it\",\"acceptance_criteria\":\"Pass\"}],\"acceptance_criteria\":\"Pass\"}],\"acceptance_criteria\":\"Pass\"},{\"name\":\"second\",\"description\":\"Second\",\"payload\":\"Do it\",\"acceptance_criteria\":\"Pass\"}]}");

        var completed = profile.Complete("session", "message").Completion
            ?? throw new InvalidOperationException("plan completion was not emitted");
        var tree = completed.TaskTree ?? throw new InvalidOperationException("task tree was not emitted");

        _ = await Assert.That(tree.OriginToolCallId).IsEmpty();
        _ = await Assert.That(tree.Revision).IsEqualTo(0UL);
        _ = await Assert.That(string.Join(',', tree.RootNodes.Select(node => node.Name))).IsEqualTo("first,second");
        _ = await Assert.That(string.Join(',', tree.RootNodes.Select(node => node.Status)))
            .IsEqualTo("Pending,Pending");
        _ = await Assert.That(string.Join(',', tree.RootNodes[0].Children.Select(node => node.Name)))
            .IsEqualTo("child-first,child-second");
        _ = await Assert.That(tree.RootNodes[0].Children[1].Children[0].Name).IsEqualTo("grandchild");
        _ = await Assert.That(tree.RootNodes[0].Children[1].Children[0].Status).IsEqualTo(AgentTaskProgressStatus.Pending);
    }

    [Test]
    public async Task Plan_completion_uses_the_user_session_artifact_and_the_main_agent_identity()
    {
        var profile = OwnerModes("user-session").Resolve(ModeRegistry.Plan);
        profile.Prepare();
        await File.WriteAllTextAsync(PlanArtifact("user-session"), "# Plan");
        await WriteValidTasks(PlanArtifact("user-session"));

        var completed = profile.Complete("main-agent-session", "message").Completion;

        if (completed is not { } emitted)
        {
            throw new InvalidOperationException("plan completion was not emitted");
        }

        _ = await Assert.That(emitted.AgentSessionId).IsEqualTo("main-agent-session");
        _ = await Assert.That(emitted.Markdown).IsEqualTo("# Plan");
    }

    [Test]
    public async Task Plan_completion_requires_and_validates_the_task_artifact()
    {
        var profile = OwnerModes("repair").Resolve(ModeRegistry.Plan);
        profile.Prepare();
        var artifact = PlanArtifact("repair");
        await File.WriteAllTextAsync(artifact, "# Plan");

        var missing = profile.Complete("session", "missing");

        _ = await Assert.That(missing.Completion).IsNull();
        _ = await Assert.That(missing.RepairDiagnostic).Contains(TaskArtifactFor(artifact));
        _ = await Assert.That(missing.RepairDiagnostic).Contains("blank");

        await File.WriteAllTextAsync(TaskArtifactFor(artifact), "{\"schema_version\":2,\"tasks\":[]}");
        var invalid = profile.Complete("session", "invalid");

        _ = await Assert.That(invalid.Completion).IsNull();
        _ = await Assert.That(invalid.RepairDiagnostic).Contains(TaskArtifactFor(artifact));
        _ = await Assert.That(invalid.RepairDiagnostic).Contains("schema_version must equal 1");
    }

    [Test]
    public async Task Plan_completion_omits_a_blank_artifact()
    {
        var profile = OwnerModes("session").Resolve(ModeRegistry.Plan);
        profile.Prepare();
        await File.WriteAllTextAsync(PlanArtifact("session"), " \n\t ");

        var outcome = profile.Complete("session", "message");

        _ = await Assert.That(outcome.Completion).IsNull();
        _ = await Assert.That(outcome.RepairDiagnostic).Contains(PlanArtifact("session"));
        _ = await Assert.That(outcome.RepairDiagnostic).Contains("Markdown plan artifact is blank");
    }

    [Test]
    public async Task Fresh_owner_state_does_not_revive_an_artifact_from_an_earlier_attempt()
    {
        var planDirectory = Path.Combine(_root, "sessions", "same-owner", "plan");
        var previous = new UserSessionModes(Registry(), TestModels.PromptTemplates, planDirectory).Resolve(ModeRegistry.Plan);
        previous.Prepare();
        var previousArtifact = PlanArtifactIn(planDirectory);
        await File.WriteAllTextAsync(previousArtifact, "stale plan");

        var current = new UserSessionModes(Registry(), TestModels.PromptTemplates, planDirectory).Resolve(ModeRegistry.Plan);
        current.Prepare();
        var artifacts = Directory.GetFiles(planDirectory, "plan-*.md");
        var currentArtifact = artifacts.Single(path => !string.Equals(path, previousArtifact, StringComparison.Ordinal));

        _ = await Assert.That(artifacts).Count().IsEqualTo(2);
        _ = await Assert.That(await File.ReadAllTextAsync(currentArtifact)).IsEmpty();
    }

    [Test]
    public async Task Plan_artifacts_are_private_random_files_in_the_owner_plan_directory()
    {
        var registry = Registry();
        var firstDirectory = Path.Combine(_root, "sessions", "first", "plan");
        var secondDirectory = Path.Combine(_root, "sessions", "second", "plan");
        var firstModes = new UserSessionModes(registry, TestModels.PromptTemplates, firstDirectory);
        var secondModes = new UserSessionModes(registry, TestModels.PromptTemplates, secondDirectory);
        var first = firstModes.Resolve(ModeRegistry.Plan);
        var firstAgain = firstModes.Resolve(ModeRegistry.Plan);
        var second = secondModes.Resolve(ModeRegistry.Plan);

        first.Prepare();
        var firstArtifact = PlanArtifactIn(firstDirectory);
        firstAgain.Prepare();
        second.Prepare();
        var secondArtifact = PlanArtifactIn(secondDirectory);
        _ = await Assert.That(Directory.GetFiles(firstDirectory, "plan-*.md")).HasSingleItem();
        _ = await Assert.That(firstArtifact).IsNotEqualTo(secondArtifact);
        _ = await Assert.That(Path.GetFileName(firstArtifact)).StartsWith("plan-").And.EndsWith(".md");
        _ = await Assert.That(Path.GetDirectoryName(firstArtifact)).IsEqualTo(firstDirectory);
    }

    private static string PlanArtifactIn(string directory) =>
        Directory.GetFiles(directory, "plan-*.md").Single();

    private static string TaskArtifactFor(string markdownArtifact) =>
        string.Concat(markdownArtifact.AsSpan(0, markdownArtifact.Length - 3), ".tasks.json");

    private static Task WriteValidTasks(string markdownArtifact) =>
        File.WriteAllTextAsync(TaskArtifactFor(markdownArtifact), "{\"schema_version\":1,\"tasks\":[{\"name\":\"work\",\"description\":\"Do work\",\"payload\":\"Implement it\",\"acceptance_criteria\":\"Tests pass\"}]}");

    private static string WriteArguments(string path, string content) =>
        string.Concat(
            "{\"path\":\"",
            JsonEncodedText.Encode(path),
            "\",\"content\":\"",
            JsonEncodedText.Encode(content),
            "\"}");

    private static AgentTurnSelection Turn(SecurityProfile securityProfile)
    {
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        return new AgentTurnSelection(
            new ModelSelector(model.Selector),
            TestModels.Resolve(model),
            TestModels.Profile(),
            securityProfile);
    }

    private UserSessionModes OwnerModes(string ownerId) => OwnerModes(ownerId, Registry());

    private UserSessionModes OwnerModes(string ownerId, ModeRegistry registry) =>
        new(registry, TestModels.PromptTemplates, Path.Combine(_root, "sessions", ownerId, "plan"));

    private string PlanArtifact(string ownerId) =>
        PlanArtifactIn(Path.Combine(_root, "sessions", ownerId, "plan"));

    private ModeRegistry Registry() => Registry([], null);

    private ModeRegistry Registry(IReadOnlyList<SandboxRule> mandatoryRules) =>
        Registry(mandatoryRules, null);

    private ModeRegistry Registry(IReadOnlyList<SandboxRule> mandatoryRules, string? defaultProfile)
    {
        var configuration = Configuration.Load(
            Path.Combine(_root, "config.yaml"),
            Path.Combine(_root, "predefined_config.yaml"));
        return new ModeRegistry(
            new ProfileRegistry(
                configuration.Profiles,
                configuration.SandboxRules,
                mandatoryRules,
                configuration.DisabledTools),
            defaultProfile ?? configuration.DefaultProfile);
    }
}
