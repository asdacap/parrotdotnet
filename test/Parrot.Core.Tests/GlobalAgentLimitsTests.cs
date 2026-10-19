using Parrot.Agent;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class GlobalAgentLimitsTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Production_sessions_apply_global_limits_and_default_to_unlimited(
        bool useDefaults,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-global-limits-tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(Path.Combine(directory, "config"));
        try
        {
            var overrides = useDefaults ? string.Empty : "disable_max_turns: false\ndisable_recursion_limit: false\n";
            var yaml = overrides + """
                profiles:
                  coordinator:
                    max_turns: 1
                  worker:
                    recursion_limit: 0
                """;
            await File.WriteAllTextAsync(Path.Combine(directory, "config", "config.yaml"), yaml, cancellationToken);
            using var provider = new SteppedProvider(
                LLMEvent.Completed("tool_calls", 1, 0, 1, string.Empty, [new LLMToolCall("read-1", "read", "{}")]),
                LLMEvent.Completed("tool_calls", 1, 0, 1, string.Empty, [new LLMToolCall("read-2", "read", "{}")]),
                LLMEvent.Completed("stop", 1, 0, 1, "done", []));
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id) { ContextWindow = 100_000 });
            using var fixture = new ProductionSessionStoreFixture(directory, model, passThroughSandbox: false);
            await using var session = await fixture.Store.Open(fixture.Router.Resolve(model.Selector));
            var root = session.Registry.SnapshotScopes().Single();
            var result = root.Session.SendAndWaitForResult(
                [ConversationPart.TextPart("work")], Identifier.MessageId(), null, null, cancellationToken);
            for (var request = 0; request < 3; request++)
            {
                await provider.Arrived(cancellationToken);
                _ = await Assert.That(provider.Requests[request].Tools.Any(tool => tool.Name == "read")).IsEqualTo(useDefaults);
                provider.Release();
            }

            _ = await Assert.That(await result).IsEqualTo("done");
            var selection = root.Session.CurrentSelection();
            var launch = new AgentLaunchRequest(
                root.Session,
                new AgentTurnSelection(selection.RequestedModel, fixture.Router.Resolve(selection.RequestedModel.Value), selection.Profile, selection.SecurityProfile),
                "worker",
                selection.RequestedModel,
                "child",
                string.Empty,
                HistoryForkSelection.Parse(string.Empty),
                new HistoryForkBoundary.AfterCompletedHistory(),
                AgentCompletionDeliveryPolicy.RetainedOnly,
                new AgentHistorySource.Parent());
            if (useDefaults)
            {
                _ = await Assert.That(root.AgentSpawner.SpawnScope(launch).Session.Depth).IsEqualTo(1);
            }
            else
            {
                _ = await Assert.That(() => root.AgentSpawner.SpawnScope(launch)).Throws<AgentRegistryException>();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
