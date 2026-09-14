using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Skills;
using Parrot.State;
using Parrot.Store;
using Parrot.Web;

namespace Parrot.Core.Tests;

internal sealed class AgentSessionServiceTests
{
    [Test]
    public async Task Production_scope_keeps_the_registered_queue_identity_through_shutdown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-session-services", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var paths = new StatePaths(
                Path.Combine(directory, "state"),
                Path.Combine(directory, "config"),
                Path.Combine(directory, "data"));
            using var diagnostics = new DiagnosticLogs(paths, FileDiagnosticLog.CreateInstanceId(), TextWriter.Null, TimeProvider.System);
            var configuration = Configuration.Load(paths.ConfigFile, paths.PredefinedConfigFile);
            var provider = new UnusedProvider();
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
            var router = TestModels.Route(model);
            var profiles = new ProfileRegistry(configuration.Profiles, configuration.SandboxRules, [], configuration.DisabledTools);
            var modes = new ModeRegistry(profiles, configuration.DefaultProfile);
            var source = new AgentSessionFactorySource(
                ProcessRunner.Locate(ExecutableLocator.Capture()),
                new Compactor(90, 30, 60_000, 1024, configuration.PromptTemplates),
                WebFetcher.Create(new PublicWebAddressPolicy()),
                configuration.ToolDefinitions,
                configuration.AgentTasks,
                configuration.RequestLimits,
                configuration.ReadOnlyExecCommandPrefixes,
                router,
                new CompositeSystemPromptProvider("test:session-services", []),
                configuration.PromptTemplates,
                static (arguments, scope) => new AgentSessionComposition(arguments, scope));
            var factory = new UserSessionFactory(
                source,
                modes,
                configuration.PromptTemplates,
                profiles,
                new SkillCatalogFactory(configuration, directory, Path.Combine(directory, "skills")),
                TimeSpan.FromSeconds(30),
                TimeProvider.System,
                TestModels.RuntimeStatusProviders,
                AgentTaskParser.ParseArtifact);
            var store = new SessionStore(paths, directory, "host", factory, router, modes, diagnostics);
            await using var session = await store.Open(router.Resolve(model.Selector));
            var scope = session.Registry.SnapshotScopes().Single();
            var queues = scope.GetService<IAgentQueues>();

            _ = await Assert.That(scope.GetService<IAgentQueues>()).IsSameReferenceAs(queues);
            _ = await Assert.That(() => scope.GetService<IAgentSession>()).Throws<InvalidOperationException>();
            _ = await Assert.That(() => scope.GetService<AgentQueues>()).Throws<InvalidOperationException>();

            var disposal = scope.DisposeAsync();
            _ = await Assert.That(scope.GetService<IAgentQueues>()).IsSameReferenceAs(queues);
            await disposal;
            _ = await Assert.That(scope.GetService<IAgentQueues>()).IsSameReferenceAs(queues);
            _ = await Assert.That(queues.IsDisposed).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
