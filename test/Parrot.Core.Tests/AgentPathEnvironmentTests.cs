using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Security;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentPathEnvironmentTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "parrot-path-environment-tests", Guid.NewGuid().ToString("n"), "space $literal; [path]");

    public AgentPathEnvironmentTests() => Directory.CreateDirectory(_workspace);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_workspace) ?? throw new InvalidOperationException("Missing fixture parent directory."), recursive: true);

    [Test]
    [Arguments("session-first", "agent-first")]
    [Arguments("session-first", "agent-second")]
    [Arguments("session-second", "agent-first")]
    public async Task Defaults_and_prompt_follow_session_identity_and_survive_reconstruction(
        string userSessionId,
        string agentSessionId)
    {
        var paths = new StatePaths(
            Path.Combine(_workspace, "state"),
            Path.Combine(_workspace, "config"),
            Path.Combine(_workspace, "data"));
        var resources = new UserSessionResources(paths, UserSessionId.Parse(userSessionId), ProjectWorkspace.FromLaunchDirectory(_workspace));
        var scratch = resources.AgentScratch(agentSessionId);
        var environment = new AgentPathEnvironment(resources, scratch);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WORKDIR"] = _workspace,
            ["SCRATCH_DIR"] = resources.ScratchRootDirectory,
            ["AGENT_SCRATCH_DIR"] = scratch.Root,
            ["AGENT_HISTORY_DIR"] = scratch.Root,
        };
        var identity = AgentIdentity.Main(agentSessionId, string.Empty, TestModels.PromptTemplates);
        var templates = new PromptTemplateCatalog(new Dictionary<string, PromptTemplate>(StringComparer.Ordinal)
        {
            ["context.agent-path-environment"] = new("environment", "{entries}", new HashSet<string>(["entries"], StringComparer.Ordinal), new HashSet<string>(["entries"], StringComparer.Ordinal)),
            ["context.agent-path-environment-entry"] = new("entry", "{name}={path}", new HashSet<string>(["name", "path"], StringComparer.Ordinal), new HashSet<string>(["name", "path"], StringComparer.Ordinal)),
            ["system.working-directory"] = new("working", "{working_directory}", new HashSet<string>(["working_directory"], StringComparer.Ordinal), new HashSet<string>(["working_directory"], StringComparer.Ordinal)),
            ["context.agent-scratch"] = new("scratch", "{path}", new HashSet<string>(["path"], StringComparer.Ordinal), new HashSet<string>(["path"], StringComparer.Ordinal)),
            ["context.agent-history"] = new("history", "{path}", new HashSet<string>(["path"], StringComparer.Ordinal), new HashSet<string>(["path"], StringComparer.Ordinal)),
        });
        var prompt = new AgentPathEnvironmentProvider(environment, templates).Materialize(identity);
        prompt.RenewEpoch();
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var selection = new AgentTurnSelection(
            new ModelSelector(model.Selector),
            TestModels.Resolve(model),
            new TestProfileFixture().Mode,
            SecurityProfile.Compose(readOnly: false, [], [], []));
        var rendered = prompt.Build(selection);
        _ = await Assert.That(new WorkingDirectoryProvider(_workspace, templates).Materialize(identity).Build(selection)).IsEqualTo(_workspace);
        _ = await Assert.That(new ScratchDirectoryProvider(scratch, templates).Materialize(identity).Build(selection)).IsEqualTo(scratch.Root);
        _ = await Assert.That(new AgentHistoryProvider(resources, templates).Materialize(identity).Build(selection))
            .IsEqualTo(resources.AgentHistoryFile(identity.SessionId));
        var displayed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in rendered.Split('\n'))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            var name = line[..separator];
            var value = line[(separator + 1)..];
            if (value.StartsWith('$'))
            {
                var slash = value.IndexOf(Path.DirectorySeparatorChar);
                var reference = slash < 0 ? value[1..] : value[1..slash];
                value = displayed[reference] + (slash < 0 ? string.Empty : value[slash..]);
            }

            displayed.Add(name, value);
        }

        var restoredResources = new UserSessionResources(paths, UserSessionId.Parse(userSessionId), ProjectWorkspace.FromLaunchDirectory(_workspace));
        var restoredEnvironment = new AgentPathEnvironment(restoredResources, restoredResources.AgentScratch(agentSessionId));
        var defaults = environment.Merge(ProcessEnvironmentOverrides.Empty).Entries.ToDictionary(StringComparer.Ordinal);
        var restored = restoredEnvironment.Materialize().ToDictionary(StringComparer.Ordinal);
        _ = await Assert.That(displayed.Count).IsEqualTo(4);
        _ = await Assert.That(defaults.Count).IsEqualTo(4);
        foreach (var entry in expected)
        {
            _ = await Assert.That(defaults[entry.Key]).IsEqualTo(entry.Value);
            _ = await Assert.That(displayed[entry.Key]).IsEqualTo(entry.Value);
            _ = await Assert.That(restored[entry.Key]).IsEqualTo(entry.Value);
        }

        var sibling = new AgentPathEnvironment(resources, resources.AgentScratch("agent-sibling")).Materialize().ToDictionary(StringComparer.Ordinal);
        var otherResources = new UserSessionResources(paths, UserSessionId.Parse("session-other"), ProjectWorkspace.FromLaunchDirectory(_workspace));
        var other = new AgentPathEnvironment(otherResources, otherResources.AgentScratch(agentSessionId)).Materialize().ToDictionary(StringComparer.Ordinal);
        _ = await Assert.That(sibling["SCRATCH_DIR"]).IsEqualTo(defaults["SCRATCH_DIR"]);
        _ = await Assert.That(sibling["AGENT_SCRATCH_DIR"]).IsNotEqualTo(defaults["AGENT_SCRATCH_DIR"]);
        _ = await Assert.That(sibling["AGENT_HISTORY_DIR"]).IsNotEqualTo(defaults["AGENT_HISTORY_DIR"]);
        _ = await Assert.That(other["SCRATCH_DIR"]).IsNotEqualTo(defaults["SCRATCH_DIR"]);
        _ = await Assert.That(other["AGENT_SCRATCH_DIR"]).IsNotEqualTo(defaults["AGENT_SCRATCH_DIR"]);

        var overrides = environment.Merge(new ProcessEnvironmentOverrides(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENT_SCRATCH_DIR"] = string.Empty,
            ["SCRATCH_DIR"] = "$WORKDIR/literal",
        })).Entries.ToDictionary(StringComparer.Ordinal);
        _ = await Assert.That(overrides["AGENT_SCRATCH_DIR"]).IsEmpty();
        _ = await Assert.That(overrides["AGENT_HISTORY_DIR"]).IsEqualTo(scratch.Root);
        _ = await Assert.That(overrides["SCRATCH_DIR"]).IsEqualTo("$WORKDIR/literal");
        _ = await Assert.That(environment.Merge(ProcessEnvironmentOverrides.Empty).Entries.ToDictionary(StringComparer.Ordinal)["SCRATCH_DIR"])
            .IsEqualTo(resources.ScratchRootDirectory);
    }
}
