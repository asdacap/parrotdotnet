using Parrot.Agent;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Skills;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class UserSessionDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-session-diagnostics-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Test]
    [Arguments("queues")]
    [Arguments("processes")]
    [Arguments("agents")]
    public async Task Partial_initialization_stops_owned_resources_and_preserves_safe_failure(string stage)
    {
        _ = Directory.CreateDirectory(_root);
        var paths = new StatePaths(_root, _root, _root);
        var resources = new UserSessionResources(paths, UserSessionId.Parse("partial"), ProjectWorkspace.FromLaunchDirectory(_root));
        using var diagnostics = FileDiagnosticLog.OpenSession(resources, "test", TextWriter.Null, TimeProvider.System);
        using var database = SessionDatabase.Open(resources.DatabasePath);
        using var lease = SessionResourceLease.Own(resources, database, diagnostics);
        var configuration = Configuration.Load(paths.ConfigFile, paths.PredefinedConfigFile);
        var profiles = new ProfileRegistry(configuration.Profiles, configuration.SandboxRules, [], configuration.DisabledTools);
        var modes = new ModeRegistry(profiles, configuration.DefaultProfile);
        var factories = new FailingAgentSessionFactories(stage);
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));

        var failure = await Assert.That(async () => await UserSession.Create(
            "partial",
            "main",
            TestModels.Resolve(model),
            configuration.DefaultProfile,
            lease,
            factories,
            new UserSessionModes(modes, configuration.PromptTemplates),
            configuration.PromptTemplates,
            profiles,
            new SkillCatalogFactory(configuration, _root, Path.Combine(_root, "skills")),
            false,
            TimeSpan.FromSeconds(30),
            TimeProvider.System)).Throws<InvalidOperationException>();

        _ = await Assert.That(failure).IsSameReferenceAs(factories.Failure);
        _ = await Assert.That(factories.Lifetime.IsCancellationRequested).IsTrue();
        if (factories.Queues is not null)
        {
            _ = await Assert.That(() => factories.Queues.Register(
                AgentIdentity.Main("probe", "main", configuration.PromptTemplates))).Throws<ObjectDisposedException>();
        }

        if (factories.Processes is not null)
        {
            _ = await Assert.That(() => factories.Processes.Prepare("probe")).Throws<InvalidOperationException>();
        }

        var log = await File.ReadAllTextAsync(resources.LogPath);
        _ = await Assert.That(log).Contains("event=\"initialization_failed\"").And.Contains("error=\"invalid_operation\"")
            .And.DoesNotContain(factories.Failure.Message);
    }

    private sealed class FailingAgentSessionFactories(string stage) : IAgentSessionFactorySource
    {
        public InvalidOperationException Failure { get; } = new("private-factory-failure-sentinel");

        public CancellationToken Lifetime { get; private set; }

        public AgentQueueCatalog? Queues { get; private set; }

        public ShellProcessOwners? Processes { get; private set; }

        public AgentQueueCatalog CreateQueueCatalog(UserSession owner)
        {
            Lifetime = owner.Lifetime;
            if (stage == "queues")
            {
                throw Failure;
            }

            Queues = new AgentQueueCatalog(owner.Resources, TestDiagnosticLog.Instance);
            return Queues;
        }

        public ShellProcessOwners CreateShellProcesses(UserSession owner)
        {
            if (stage == "processes")
            {
                throw Failure;
            }

            Processes = new ShellProcessOwners(owner.Resources, ProcessRunner.Locate(ExecutableLocator.Capture()), TestDiagnosticLog.Instance, owner.Lifetime);
            return Processes;
        }

        public IAgentSessionFactory Create(UserSession owner) => throw Failure;
    }
}
