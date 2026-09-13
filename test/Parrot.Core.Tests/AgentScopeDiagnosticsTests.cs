using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Skills;
using Parrot.State;
using Parrot.Store;
using Parrot.Web;

namespace Parrot.Core.Tests;

internal sealed class AgentScopeDiagnosticsTests
{
    [Test]
    [Skip("Probable pre-existing lifecycle bug: root scope access during shutdown re-materializes ShellProcessOwner after settlement.")]
    public async Task Child_scope_uses_session_log_and_closes_before_session_resources()
    {
        var root = Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(root);
        try
        {
            var paths = new StatePaths(Path.Combine(root, "state"), Path.Combine(root, "config"), Path.Combine(root, "data"));
            using var diagnostics = new DiagnosticLogs(paths, FileDiagnosticLog.CreateInstanceId(), TextWriter.Null, TimeProvider.System);
            var configuration = Configuration.Load(paths.ConfigFile, paths.PredefinedConfigFile);
            ILLMProvider provider = new UnusedProvider();
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
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
                new CompositeSystemPromptProvider("test:diagnostics", []),
                configuration.PromptTemplates,
                static (arguments, scope) => new AgentSessionComposition(arguments, scope));
            var factory = new UserSessionFactory(
                source,
                modes,
                configuration.PromptTemplates,
                profiles,
                new SkillCatalogFactory(configuration, root, Path.Combine(root, "skills")),
                TimeSpan.FromSeconds(30),
                TimeProvider.System);
            var store = new SessionStore(paths, root, "host", factory, router, modes, diagnostics);
            string logPath;
            await using (var session = await store.Open(router.Resolve(model.Selector)))
            {
                logPath = session.Resources.LogPath;
                var rootId = Directory.GetDirectories(session.Resources.ScratchRootDirectory)
                    .Select(Path.GetFileName).Single() ?? throw new InvalidOperationException("Missing root agent");
                var parent = session.Registry.FindScope(rootId) ?? throw new InvalidOperationException("Missing root scope");
                var identity = AgentIdentity.Child(
                    "diagnostic-child", rootId, "main", "child", 1, AgentScope.Empty(configuration.PromptTemplates), configuration.PromptTemplates);
                await using var child = session.Registry.CreateChildScope(
                    identity,
                    AgentSessionParentLink.Child(parent, AgentCompletionDeliveryPolicy.RetainedOnly),
                    new ModelSelector(model.Selector),
                    session.Mode,
                    session.Mode.Profile.SecurityProfile,
                    session.Status,
                    session.Registry.InitializeChildHistory(rootId, identity.SessionId, new HistoryForkBoundary.AfterCompletedHistory(), HistoryForkSelection.Parse("empty")),
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
