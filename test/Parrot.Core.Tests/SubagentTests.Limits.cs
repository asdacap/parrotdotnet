using Parrot.Agent;
using Parrot.Config;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed partial class SubagentTests
{
    [Test]
    [Arguments(false, "worker", 0, 0)]
    [Arguments(true, "worker", 0, 0)]
    [Arguments(false, "agent-task-payload", 0, 0)]
    [Arguments(true, "agent-task-payload", 0, 0)]
    [Arguments(false, "worker", 4, 10)]
    [Arguments(true, "worker", 4, 10)]
    public async Task Global_recursion_switch_controls_profile_and_depth_limits(
        bool disabled,
        string profile,
        int parentDepth,
        int recursionLimit,
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = new RouterFixture(provider, []).Router;
        var configuredProfiles = TestModels.Profiles.ToDictionary(
            entry => entry.Key,
            entry => entry.Value,
            StringComparer.Ordinal);
        configuredProfiles[profile] = configuredProfiles[profile] with { RecursionLimit = recursionLimit };
        var profiles = new ProfileRegistry(configuredProfiles, [], [], new HashSet<string>(StringComparer.Ordinal));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router), _broker, _repository, profiles, TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, parentDepth, "parent-id", registry, cancellationToken);
        var parentScope = TestModels.ScopeOf(parent);
        if (parentScope.ParentScope.Parent is { } ancestor)
        {
            _ = await Assert.That(ancestor.ChildRegistry.TryAdd(parentScope)).IsTrue();
        }

        await using var spawner = new AgentSpawner(
            parent.Identity, registry, parentScope.ParentScope, parentScope.ChildRegistry, new AgentLimitsConfig(false, disabled));
        var request = new AgentLaunchRequest(
            parent,
            new TurnFixture(parent, router).Selection,
            profile,
            parent.CurrentSelection().RequestedModel,
            "limited-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly,
            new AgentHistorySource.Parent());

        if (disabled)
        {
            var child = spawner.SpawnScope(request);
            _ = await Assert.That(child.Session.Depth).IsEqualTo(parentDepth + 1);
            _ = await Assert.That(child.Session.CurrentSelection().Profile.Id).IsEqualTo(profile);
            _ = await Assert.That(spawner.SpawnOrResumeScope(request)).IsSameReferenceAs(child);
        }
        else
        {
            var error = await Assert.That(() => spawner.SpawnScope(request)).Throws<AgentRegistryException>();
            _ = await Assert.That(error?.Message).IsEqualTo(parentDepth == 4
                ? "subagent depth limit reached"
                : "subagent profile recursion limit reached");
            _ = await Assert.That(parentScope.ChildRegistry.SnapshotDescendants()).IsEmpty();
        }
    }
}
