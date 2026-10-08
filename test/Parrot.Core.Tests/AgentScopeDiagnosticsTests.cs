using Parrot.Agent;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentScopeDiagnosticsTests
{
    [Test]
    public async Task Child_scope_uses_session_log_and_closes_before_session_resources()
    {
        var root = Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(root);
        try
        {
            ILLMProvider provider = new UnusedProvider();
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
            using var fixture = new ProductionSessionStoreFixture(root, model, passThroughSandbox: false);
            string logPath;
            await using (var session = await fixture.Store.Open(fixture.Router.Resolve(model.Selector)))
            {
                logPath = session.Resources.LogPath;
                var parent = session.Registry.SnapshotScopes().Single();
                _ = await Assert.That(Directory.GetDirectories(session.Resources.AgentsDirectory).Select(Path.GetFileName).Single())
                    .IsEqualTo(parent.Session.Name);
                var identity = AgentIdentity.Child(
                    "diagnostic-child", parent.Session.Identity, "child", 1, AgentScope.Empty(fixture.Configuration.PromptTemplates), AgentPolicyLineage.Root(), fixture.Configuration.PromptTemplates);
                await using var child = session.Registry.CreateChildScope(
                    identity,
                    AgentSessionParentLink.Child(parent, AgentCompletionDeliveryPolicy.RetainedOnly, session.Registry.ReserveRetainedAgent()),
                    new ModelSelector(model.Selector),
                    session.Mode.Profile,
                    session.Mode.Profile.SecurityProfile,
                    session.Registry.InitializeChildHistory(identity, new HistoryForkBoundary.AfterCompletedHistory(), HistoryForkSelection.Parse("empty"), new AgentHistorySource.Parent()),
                    session.Lifetime);
                _ = await Assert.That(child.Session.SessionId).IsEqualTo(identity.SessionId);
                var text = await File.ReadAllTextAsync(logPath);
                _ = await Assert.That(text).Contains("event=\"created\"").And.Contains("agent=\"diagnostic-child\"")
                    .And.Contains($"session=\"{session.Id}\"");
            }

            var closed = await File.ReadAllTextAsync(logPath);
            var childClosed = closed.IndexOf("event=\"closed\"", StringComparison.Ordinal);
            var producersStopped = closed.IndexOf("event=\"producers_stopped\"", StringComparison.Ordinal);
            _ = await Assert.That(childClosed >= 0 && producersStopped > childClosed).IsTrue();
            using var exclusive = new FileStream(logPath, FileMode.Open, FileAccess.Write, FileShare.None);
            _ = await Assert.That(exclusive.CanWrite).IsTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
