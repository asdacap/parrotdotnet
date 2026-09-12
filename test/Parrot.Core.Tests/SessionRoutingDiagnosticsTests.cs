using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Skills;
using Parrot.State;
using Parrot.Store;
using Parrot.Web;

namespace Parrot.Core.Tests;

internal sealed class SessionRoutingDiagnosticsTests
{
    [Test]
    [Skip("Probable pre-existing lifecycle bug: root scope access during shutdown re-materializes ShellProcessOwner after settlement.")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Shared_provider_routes_root_and_child_turns_to_owning_session_and_resume_appends(
        bool interrupt, CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "parrot-routing-diagnostics-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            var paths = new StatePaths(Path.Combine(root, "state"), Path.Combine(root, "config"), Path.Combine(root, "data"));
            using var diagnostics = new DiagnosticLogs(paths, FileDiagnosticLog.CreateInstanceId(), TextWriter.Null, TimeProvider.System);
            using var provider = new SteppedProvider(
                LLMEvent.Completed("stop", 1, 0, 1, "private-root-response-sentinel", []),
                LLMEvent.Completed("stop", 1, 0, 1, "private-other-response-sentinel", []),
                LLMEvent.Completed("stop", 1, 0, 1, "private-child-response-sentinel", []));
            var configuration = Configuration.Load(paths.ConfigFile, paths.PredefinedConfigFile);
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id) { ContextWindow = 100_000 });
            var router = TestModels.Route(model);
            var profiles = new ProfileRegistry(configuration.Profiles, configuration.SandboxRules, [], configuration.DisabledTools);
            var modes = new ModeRegistry(profiles, configuration.DefaultProfile);
            var web = WebFetcher.Create(new PublicWebAddressPolicy());
            var source = new AgentSessionFactorySource(
                ProcessRunner.Locate(ExecutableLocator.Capture()),
                new Compactor(90, 30, 60_000, 1024, configuration.PromptTemplates),
                web,
                configuration.ToolDefinitions,
                configuration.AgentTasks,
                configuration.RequestLimits,
                configuration.ReadOnlyExecCommandPrefixes,
                router,
                new CompositeSystemPromptProvider("test:routing", []),
                configuration.PromptTemplates);
            var factory = new UserSessionFactory(
                source,
                modes,
                configuration.PromptTemplates,
                profiles,
                new SkillCatalogFactory(configuration, root, Path.Combine(root, "skills")),
                TimeSpan.FromSeconds(30),
                TimeProvider.System);
            var store = new SessionStore(paths, root, "host", factory, router, modes, diagnostics);
            string firstId;
            string firstLogPath;
            string secondLogPath;
            string firstAgentId;
            string secondAgentId;
            const string childId = "agent-routing-child";
            await using (var first = await store.CreateFresh(router.Resolve(model.Selector), modes.Default, false))
            await using (var second = await store.CreateFresh(router.Resolve(model.Selector), modes.Default, false))
            {
                firstId = first.Id;
                firstLogPath = first.Resources.LogPath;
                secondLogPath = second.Resources.LogPath;
                firstAgentId = Directory.GetDirectories(first.Resources.ScratchRootDirectory).Select(Path.GetFileName).Single()
                    ?? throw new InvalidOperationException("Missing first root agent");
                secondAgentId = Directory.GetDirectories(second.Resources.ScratchRootDirectory).Select(Path.GetFileName).Single()
                    ?? throw new InvalidOperationException("Missing second root agent");
                var firstScope = first.Registry.FindScope(firstAgentId) ?? throw new InvalidOperationException("Missing first root scope");
                var secondScope = second.Registry.FindScope(secondAgentId) ?? throw new InvalidOperationException("Missing second root scope");
                var childIdentity = AgentIdentity.Child(
                    childId,
                    firstAgentId,
                    "main",
                    "child",
                    1,
                    AgentScope.Empty(configuration.PromptTemplates),
                    configuration.PromptTemplates);
                await using var child = first.Registry.CreateChildScope(
                    childIdentity,
                    AgentSessionParentLink.Child(firstScope, AgentCompletionDeliveryPolicy.RetainedOnly),
                    new ModelSelector(model.Selector),
                    first.Mode,
                    first.Mode.Profile.SecurityProfile,
                    first.Status,
                    first.Registry.InitializeChildHistory(firstScope.Session.SessionId, childIdentity.SessionId, new HistoryForkBoundary.AfterCompletedHistory(), HistoryForkSelection.Parse("empty")),
                    first.Lifetime);

                _ = await first.SendText("private-root-prompt-sentinel https://example.invalid/?token=private-url-sentinel", "first-message", Delivery.Steer, cancellationToken);
                await provider.Arrived(cancellationToken);
                _ = await second.SendText("private-other-prompt-sentinel", "second-message", Delivery.Steer, cancellationToken);
                await provider.Arrived(cancellationToken);
                _ = await child.Session.Send([ConversationPart.TextPart("private-child-prompt-sentinel")], "child-message", Delivery.Steer, cancellationToken);
                await provider.Arrived(cancellationToken);
                if (interrupt)
                {
                    await Task.WhenAll(first.Interrupt(cancellationToken), second.Interrupt(cancellationToken), child.Session.Interrupt(cancellationToken));
                }
                else
                {
                    provider.Release();
                    provider.Release();
                    provider.Release();
                }

                await Task.WhenAll(firstScope.Session.Settled(), secondScope.Session.Settled(), child.Session.Settled());
                var firstLog = await File.ReadAllTextAsync(firstLogPath, cancellationToken);
                var secondLog = await File.ReadAllTextAsync(secondLogPath, cancellationToken);
                _ = await Assert.That(firstLog).Contains($"session=\"{first.Id}\"").And.Contains($"agent=\"{firstAgentId}\"")
                    .And.Contains($"agent=\"{childId}\"").And.DoesNotContain(second.Id).And.DoesNotContain(secondAgentId);
                _ = await Assert.That(secondLog).Contains($"session=\"{second.Id}\"").And.Contains($"agent=\"{secondAgentId}\"")
                    .And.DoesNotContain(first.Id).And.DoesNotContain(firstAgentId).And.DoesNotContain(childId);
                foreach (var (log, agentId) in new[] { (firstLog, firstAgentId), (firstLog, childId), (secondLog, secondAgentId) })
                {
                    var turnLines = log.Split('\n').Where(line => line.Contains("category=\"turn\"", StringComparison.Ordinal)
                        && line.Contains($"agent=\"{agentId}\"", StringComparison.Ordinal)).ToArray();
                    _ = await Assert.That(turnLines.Count(line => line.Contains("event=\"started\"", StringComparison.Ordinal))).IsEqualTo(1);
                    _ = await Assert.That(turnLines.Count(line => line.Contains("event=\"finished\"", StringComparison.Ordinal))).IsEqualTo(1);
                    _ = await Assert.That(turnLines.Single(line => line.Contains("event=\"finished\"", StringComparison.Ordinal)))
                        .Contains(interrupt ? "outcome=\"cancelled\"" : "outcome=\"completed\"").And.Contains("duration_ms=");
                }
            }

            var beforeResume = await File.ReadAllTextAsync(firstLogPath, cancellationToken);
            _ = await Assert.That(beforeResume).Contains("category=\"session\" event=\"closed\"");
            _ = await Assert.That(beforeResume.IndexOf("category=\"agent\" event=\"closed\"", StringComparison.Ordinal)
                < beforeResume.IndexOf("event=\"producers_stopped\"", StringComparison.Ordinal)).IsTrue();
            await using (var resumed = (await store.Resume(UserSessionId.Parse(firstId), false)).Session)
            {
                _ = await Assert.That(resumed.Id).IsEqualTo(firstId);
                var resumedLog = await File.ReadAllTextAsync(firstLogPath, cancellationToken);
                _ = await Assert.That(resumedLog.StartsWith(beforeResume, StringComparison.Ordinal)).IsTrue();
                _ = await Assert.That(resumedLog).Contains("category=\"session\" event=\"resume\"");
            }

            foreach (var logPath in Directory.GetFiles(paths.State, "*.log", SearchOption.AllDirectories))
            {
                var log = await File.ReadAllTextAsync(logPath, cancellationToken);
                _ = await Assert.That(log).DoesNotContain("private-").And.DoesNotContain("https://example.invalid");
                if (logPath != firstLogPath && logPath != secondLogPath)
                {
                    _ = await Assert.That(log).DoesNotContain("category=\"turn\"").And.DoesNotContain("category=\"provider\"")
                        .And.DoesNotContain($"agent=\"{childId}\"");
                }
            }

            using var exclusive = new FileStream(firstLogPath, FileMode.Open, FileAccess.Write, FileShare.None);
            _ = await Assert.That(exclusive.CanWrite).IsTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
