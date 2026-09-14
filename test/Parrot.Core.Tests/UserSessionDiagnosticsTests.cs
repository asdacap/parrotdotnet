using Parrot.Agent;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Questions;
using Parrot.Skills;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class UserSessionDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-session-diagnostics-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Test]
    public async Task Partial_initialization_stops_owned_resources_and_preserves_safe_failure()
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
        var factories = new FailingAgentSessionFactories();
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
            TimeProvider.System,
            static () => new EventBroker(),
            TestModels.RuntimeStatusProviders)).Throws<InvalidOperationException>();

        _ = await Assert.That(failure).IsSameReferenceAs(factories.Failure);
        _ = await Assert.That(factories.Lifetime.IsCancellationRequested).IsTrue();
        var questions = factories.Questions ?? throw new InvalidOperationException("The factory did not capture the session's question broker.");
        _ = await Assert.That(async () => await questions.Ask(
            [new QuestionDefinition("probe", "Continue?", [new Parrot.Questions.QuestionOption("Yes", string.Empty), new Parrot.Questions.QuestionOption("No", string.Empty)], false, false)], CancellationToken.None))
            .Throws<ObjectDisposedException>();

        var log = await File.ReadAllTextAsync(resources.LogPath);
        _ = await Assert.That(log).Contains("event=\"initialization_failed\"").And.Contains("error=\"invalid_operation\"")
            .And.DoesNotContain(factories.Failure.Message);
    }

    private sealed class FailingAgentSessionFactories : IAgentSessionFactorySource
    {
        public InvalidOperationException Failure { get; } = new("private-factory-failure-sentinel");

        public CancellationToken Lifetime { get; private set; }

        public IQuestionBroker? Questions { get; private set; }

        public IAgentSessionFactory Create(IUserSession owner)
        {
            Lifetime = owner.Lifetime;
            Questions = owner.Questions;
            throw Failure;
        }
    }
}
