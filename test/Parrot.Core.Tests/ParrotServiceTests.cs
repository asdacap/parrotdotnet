using Grpc.Core;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Core.Tests;

// The contract's half of the queue: how a prompt is admitted, what comes back,
// and what is refused.
internal sealed class ParrotServiceTests : IDisposable
{
    private const string Selection = "scripted/model";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));

    private readonly Configuration _configuration;
    private readonly ProviderRegistry _registry;
    private readonly ModelAliasCatalog _catalog;
    private readonly ModelRouter _router;

    public ParrotServiceTests()
    {
        _registry = Registry();
        _configuration = Configuration.Load(Path.Combine(_root, "config.yaml"), Path.Combine(_root, "predefined_config.yaml"));
        _catalog = new ModelAliasCatalog(_registry, _configuration.ModelAliases.Select(alias =>
            new ModelAliasDefinition(
                alias.Key,
                alias.Value.ModelString,
                alias.Value.Usage,
                alias.Value.AugmentSystemPrompt)));
        _router = new ModelRouter(_registry, _catalog, Selection);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task A_prompt_is_answered_with_the_admission_it_made(CancellationToken cancellationToken)
    {
        var sessions = new DirectAgentSessions();
        using var store = Store(sessions);
        await using var service = Service(store);
        var context = new InProcessServerCallContext(cancellationToken);

        var session = await service.CreateSession(new CreateSessionRequest { Model = Selection }, context);
        var admitted = await service.SendMessage(Send(session.Id, "hello", "msg-1"), context);

        _ = await Assert.That(sessions.Identities.Single().Name).IsEqualTo("main");
        _ = await Assert.That(admitted.Created).IsTrue();
        _ = await Assert.That(admitted.MessageId).IsEqualTo("msg-1");
        _ = await Assert.That(admitted.InputId).IsNotEmpty();

        // The same prompt again, as a client that lost its connection would
        // send it: the id it names is the one already admitted.
        var again = await service.SendMessage(Send(session.Id, "hello", "msg-1"), context);

        _ = await Assert.That(again.Created).IsFalse();
        _ = await Assert.That(again.InputId).IsEqualTo(admitted.InputId);
    }

    [Test]
    public async Task Modes_are_listed_created_updated_and_validated(CancellationToken cancellationToken)
    {
        using var store = Store();
        await using var service = Service(store);
        var context = new InProcessServerCallContext(cancellationToken);
        var client = new GeneratedParrot.ParrotClient(new InProcessCallInvoker(service));

        var listed = await client.ListModesAsync(new ListModesRequest(), cancellationToken: cancellationToken);
        var defaulted = await service.CreateSession(new CreateSessionRequest { Model = Selection }, context);
        var created = await service.CreateSession(
            new CreateSessionRequest { Model = Selection, Mode = ModeRegistry.Plan }, context);
        var updated = await service.UpdateSession(
            new UpdateSessionRequest { UserSessionId = created.Id, Mode = ModeRegistry.Query }, context);
        var carried = await service.UpdateSession(
            new UpdateSessionRequest { UserSessionId = created.Id, Model = Selection }, context);
        var refused = await Assert.That(async () => await service.UpdateSession(
            new UpdateSessionRequest { UserSessionId = created.Id, Mode = ModeRegistry.Plan, Model = "unknown/model" },
            context)).Throws<RpcException>();
        var afterRefusal = await service.UpdateSession(
            new UpdateSessionRequest { UserSessionId = created.Id }, context);

        _ = await Assert.That(string.Join(",", listed.Modes.Select(mode => mode.Id)))
            .IsEqualTo("build,plan,query");
        _ = await Assert.That(defaulted.Mode).IsEqualTo(ModeRegistry.Build);
        _ = await Assert.That(created.Mode).IsEqualTo(ModeRegistry.Plan);
        _ = await Assert.That(updated.Mode).IsEqualTo(ModeRegistry.Query);
        _ = await Assert.That(carried.Mode).IsEqualTo(ModeRegistry.Query);
        _ = await Assert.That(refused?.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        _ = await Assert.That(afterRefusal.Mode).IsEqualTo(ModeRegistry.Query);
    }

    [Test]
    public async Task In_process_question_calls_are_routed(CancellationToken cancellationToken)
    {
        using var store = Store();
        await using var service = Service(store);
        var client = new GeneratedParrot.ParrotClient(new InProcessCallInvoker(service));
        var session = await client.CreateSessionAsync(
            new CreateSessionRequest { Model = Selection }, cancellationToken: cancellationToken);

        var listed = await client.ListPendingQuestionsAsync(
            new ListPendingQuestionsRequest { UserSessionId = session.Id },
            cancellationToken: cancellationToken);
        var replied = await Assert.That(async () => await client.ReplyQuestionAsync(
            new ReplyQuestionRequest { UserSessionId = session.Id, QuestionRequestId = "missing" },
            cancellationToken: cancellationToken)).Throws<RpcException>();
        var rejected = await Assert.That(async () => await client.RejectQuestionAsync(
            new RejectQuestionRequest { UserSessionId = session.Id, QuestionRequestId = "missing" },
            cancellationToken: cancellationToken)).Throws<RpcException>();

        _ = await Assert.That(listed.Questions).IsEmpty();
        _ = await Assert.That(replied?.StatusCode).IsEqualTo(StatusCode.NotFound);
        _ = await Assert.That(rejected?.StatusCode).IsEqualTo(StatusCode.NotFound);
    }

    [Test]
    public async Task Model_variants_are_listed_selected_and_unlisted_models_are_persisted(
        CancellationToken cancellationToken)
    {
        using var store = Store();
        await using var service = Service(store);
        var context = new InProcessServerCallContext(cancellationToken);

        var listed = await service.ListModels(new ListModelsRequest(), context);
        var created = await service.CreateSession(
            new CreateSessionRequest { Model = "scripted/vendor/model/high" }, context);
        var updated = await service.UpdateSession(
            new UpdateSessionRequest { UserSessionId = created.Id, Model = Selection }, context);
        var unlisted = await service.UpdateSession(
            new UpdateSessionRequest { UserSessionId = created.Id, Model = "scripted/model/missing" }, context);
        var meta = store.Index.List().Single(item => item.Id == created.Id);

        _ = await Assert.That(string.Join(",", listed.Models.Single(model => model.Id == "vendor/model")
            .Variants.Select(variant => $"{variant.Name}:{variant.ReasoningEffort}")))
            .IsEqualTo("low:low,high:xhigh");
        _ = await Assert.That(created.Model).IsEqualTo("scripted/vendor/model/high");
        _ = await Assert.That(updated.Model).IsEqualTo(Selection);
        _ = await Assert.That(unlisted.Model).IsEqualTo("scripted/model/missing");
        _ = await Assert.That(meta.Model).IsEqualTo("scripted/model/missing");
        _ = await Assert.That(meta.RootAgentName).IsEqualTo("main");
    }

    [Test]
    public async Task Model_aliases_are_listed_configured_routed_and_validated(CancellationToken cancellationToken)
    {
        using var store = Store();
        await using var service = Service(store);
        var client = new GeneratedParrot.ParrotClient(new InProcessCallInvoker(service));

        var before = await client.ListModelAliasesAsync(
            new ListModelAliasesRequest(), cancellationToken: cancellationToken);
        var configured = await client.ConfigureModelAliasAsync(
            new ConfigureModelAliasRequest { Name = "high_llm", ModelString = Selection },
            cancellationToken: cancellationToken);
        var after = await client.ListModelAliasesAsync(
            new ListModelAliasesRequest(), cancellationToken: cancellationToken);
        var session = await client.CreateSessionAsync(
            new CreateSessionRequest { Model = "high_llm" }, cancellationToken: cancellationToken);
        var updated = await client.UpdateSessionAsync(
            new UpdateSessionRequest { UserSessionId = session.Id, Model = "high_llm" },
            cancellationToken: cancellationToken);
        var meta = store.Index.List().Single(item => item.Id == session.Id);
        var refused = await Assert.That(async () => await client.ConfigureModelAliasAsync(
            new ConfigureModelAliasRequest { Name = "missing", ModelString = Selection },
            cancellationToken: cancellationToken)).Throws<RpcException>();

        _ = await Assert.That(string.Join(",", before.Aliases.Select(alias => alias.Name)))
            .IsEqualTo("high_llm,low_llm,medium_llm,xhigh_llm");
        _ = await Assert.That(configured.Alias.Name).IsEqualTo("high_llm");
        _ = await Assert.That(configured.Alias.ModelString).IsEqualTo(Selection);
        _ = await Assert.That(after.Aliases.Single(alias => alias.Name == "high_llm").ModelString)
            .IsEqualTo(Selection);
        _ = await Assert.That(session.Model).IsEqualTo("high_llm");
        _ = await Assert.That(updated.Model).IsEqualTo("high_llm");
        _ = await Assert.That(meta.Selector).IsEqualTo("high_llm");
        _ = await Assert.That(meta.ProviderId).IsEqualTo("scripted");
        _ = await Assert.That(meta.Model).IsEqualTo(Selection);
        _ = await Assert.That(refused?.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        _ = await Assert.That(await File.ReadAllTextAsync(Path.Combine(_root, "config.yaml"), cancellationToken))
            .Contains("model_string: scripted/model");
    }

    [Test]
    public async Task A_persistence_failure_does_not_publish_an_alias(CancellationToken cancellationToken)
    {
        var registry = Registry();
        var blockedParent = Path.Combine(_root, "not-a-directory");
        _ = Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(blockedParent, "file", cancellationToken);
        var configuration = Configuration.Load(Path.Combine(blockedParent, "config.yaml"), Path.Combine(_root, "predefined_config.yaml"));
        var catalog = new ModelAliasCatalog(registry, configuration.ModelAliases.Select(alias =>
            new ModelAliasDefinition(
                alias.Key,
                alias.Value.ModelString,
                alias.Value.Usage,
                alias.Value.AugmentSystemPrompt)));
        var configurator = new ModelAliasConfigurator(configuration, catalog);

        _ = await Assert.That(() => configurator.Configure("high_llm", Selection)).Throws<IOException>();
        _ = await Assert.That(catalog.Capture().Find("high_llm")?.ModelString).IsEmpty();
    }

    [Test]
    public async Task A_prompt_with_no_delivery_is_refused(CancellationToken cancellationToken)
    {
        using var store = Store();
        await using var service = Service(store);
        var context = new InProcessServerCallContext(cancellationToken);

        var session = await service.CreateSession(new CreateSessionRequest { Model = Selection }, context);

        var refused = await Assert.That(async () => await service.SendMessage(
            new SendMessageRequest { UserSessionId = session.Id, Text = "hello" }, context))
            .Throws<RpcException>();

        _ = await Assert.That(refused?.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
    }

    [Test]
    public async Task A_session_nobody_opened_can_be_neither_prompted_nor_interrupted(
        CancellationToken cancellationToken)
    {
        using var store = Store();
        await using var service = Service(store);
        var context = new InProcessServerCallContext(cancellationToken);

        var prompted = await Assert.That(async () =>
            await service.SendMessage(Send("no-such-session", "hello", "msg-1"), context)).Throws<RpcException>();

        var interrupted = await Assert.That(async () => await service.Interrupt(
            new InterruptRequest { UserSessionId = "no-such-session" }, context)).Throws<RpcException>();

        _ = await Assert.That(prompted?.StatusCode).IsEqualTo(StatusCode.NotFound);
        _ = await Assert.That(interrupted?.StatusCode).IsEqualTo(StatusCode.NotFound);
    }

    private static SendMessageRequest Send(string userSessionId, string text, string messageId) =>
        new()
        {
            UserSessionId = userSessionId,
            Text = text,
            MessageId = messageId,
            Delivery = Delivery.Steer,
        };

    private static ProviderRegistry Registry()
    {
        var models = new LLMModel[]
        {
            new("model", "scripted"),
            new("vendor/model", "scripted")
            {
                Capabilities = new ModelCapabilities(
                    true,
                    true,
                    ["text"],
                    [
                        new Parrot.Llm.ModelVariant("low", "low"),
                        new Parrot.Llm.ModelVariant("high", "xhigh"),
                    ]),
            },
        };
        var provider = new ScriptedProvider("an answer") { Models = models };

        return new ProviderRegistry(
            [provider],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal) { ["scripted"] = models });
    }

    private ParrotService Service(SessionStore store) => new(
        _router,
        _registry,
        new ModelAliasConfigurator(_configuration, _catalog),
        store,
        Modes());

    private ModeRegistry Modes() => new(Path.Combine(_root, "plans"), _configuration.SandboxRules, _configuration.Profiles);

    private SessionStore Store() => Store(new DirectAgentSessions());

    private SessionStore Store(DirectAgentSessions sessions)
    {
        sessions.Use(_router);
        return new(
            _root,
            Path.Combine(_root, "work"),
            "host",
            new UserSessionFactory(sessions, Modes()),
            _router);
    }
}
