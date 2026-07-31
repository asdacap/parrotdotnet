using Grpc.Core;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Permissions;
using Parrot.Protocol;
using Parrot.Queues;
using Parrot.State;
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
        _ = Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "config.yaml");
        var configContent = """
            provider_model_alias_defaults:
              scripted:
                low_llm: scripted/low
                medium_llm: scripted/medium
                high_llm: scripted/high
                xhigh_llm: scripted/xhigh
            """;
        File.WriteAllText(configPath, configContent);
        _configuration = Configuration.Load(Path.Combine(_root, "config.yaml"), Path.Combine(_root, "predefined_config.yaml"));
        _catalog = new ModelAliasCatalog(_registry, _configuration.ModelAliases.Select(alias =>
            new ModelAliasDefinition(
                alias.Key,
                alias.Value.ModelString,
                alias.Value.Usage,
                alias.Value.AugmentSystemPrompt,
                alias.Value.Icon is null ? null : ModelAliasIcon.Parse(alias.Value.Icon.Glyph, alias.Value.Icon.Color))));
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
        var store = Store(sessions);
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
        var store = Store();
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
    public async Task Sessions_are_listed_from_the_server_catalog_with_hosted_state(
        CancellationToken cancellationToken)
    {
        var store = Store();
        PublishMeta("user-session-inactive", "main-2", "scripted/inactive", "plan", "2026-07-28T02:00:00Z");
        var corruptDirectory = Path.Combine(_root, "sessions", "user-session-corrupt");
        _ = Directory.CreateDirectory(corruptDirectory);
        await File.WriteAllTextAsync(Path.Combine(corruptDirectory, "meta.json"), "{broken", cancellationToken);
        await using var service = Service(store);
        var client = new GeneratedParrot.ParrotClient(new InProcessCallInvoker(service));
        var active = await client.CreateSessionAsync(
            new CreateSessionRequest { Model = Selection }, cancellationToken: cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "sessions", active.Id, "meta.json"),
            "{broken",
            cancellationToken);

        var listed = await client.ListSessionsAsync(
            new ListSessionsRequest(), cancellationToken: cancellationToken);

        var activeSummary = listed.Sessions.Single(item => item.UserSessionId == active.Id);
        var inactiveSummary = listed.Sessions.Single(item => item.UserSessionId == "user-session-inactive");
        var corruptSummary = listed.Sessions.Single(item => item.UserSessionId == "user-session-corrupt");
        _ = await Assert.That(activeSummary.State).IsEqualTo(SessionState.Active);
        _ = await Assert.That(inactiveSummary.State).IsEqualTo(SessionState.Inactive);
        _ = await Assert.That(inactiveSummary.Model).IsEqualTo("scripted/inactive");
        _ = await Assert.That(inactiveSummary.Mode).IsEqualTo("plan");
        _ = await Assert.That(inactiveSummary.RootAgentName).IsEqualTo("main-2");
        _ = await Assert.That(corruptSummary.State).IsEqualTo(SessionState.Corrupt);
        _ = await Assert.That(corruptSummary.Model).IsEmpty();
    }

    [Test]
    public async Task Create_session_creates_fresh_instead_of_resuming_an_inactive_session(
        CancellationToken cancellationToken)
    {
        var context = new InProcessServerCallContext(cancellationToken);
        string firstId;

        await using (var firstService = Service(Store()))
        {
            var first = await firstService.CreateSession(
                new CreateSessionRequest { Model = Selection }, context);
            firstId = first.Id;
        }

        await using var secondService = Service(Store());
        var second = await secondService.CreateSession(
            new CreateSessionRequest { Model = Selection }, context);

        _ = await Assert.That(second.Id).IsNotEqualTo(firstId);
        _ = await Assert.That(second.Loaded).IsFalse();
        _ = await Assert.That(FindMeta(firstId).RootAgentName).IsEqualTo("main");
        _ = await Assert.That(FindMeta(second.Id).RootAgentName).IsEqualTo("main");
    }

    [Test]
    public async Task In_process_question_calls_are_routed(CancellationToken cancellationToken)
    {
        var store = Store();
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
    public async Task In_process_permission_calls_map_pending_replies_and_errors(
        CancellationToken cancellationToken)
    {
        var sessions = new DirectAgentSessions();
        var store = Store(sessions);
        await using var service = Service(store);
        var client = new GeneratedParrot.ParrotClient(new InProcessCallInvoker(service));
        var created = await client.CreateSessionAsync(
            new CreateSessionRequest { Model = Selection, InteractivePermissions = true },
            cancellationToken: cancellationToken);
        _ = await client.SendMessageAsync(
            Send(created.Id, "initialize", "msg-permission"),
            cancellationToken: cancellationToken);
        var directory = EnsureDirectory(Path.Combine(_root, "permission-target"));
        var file = Path.Combine(directory, "generated.txt");
        await File.WriteAllTextAsync(file, "before", cancellationToken);
        var request = sessions.Owners.Single().Permissions.Request(
            sessions.Sessions.Single(),
            "update generated files",
            [SandboxWriteTarget.Resolve(directory), SandboxWriteTarget.Resolve(file)],
            cancellationToken);
        await WaitForPermission(sessions.Owners.Single(), cancellationToken);

        var listed = await client.ListPendingPermissionsAsync(
            new ListPendingPermissionsRequest { UserSessionId = created.Id },
            cancellationToken: cancellationToken);
        var pending = listed.Permissions.Single();
        var invalid = await Assert.That(async () => await client.ReplyPermissionAsync(
            new ReplyPermissionRequest
            {
                UserSessionId = created.Id,
                PermissionRequestId = pending.Id,
                ChoiceValue = "missing",
            },
            cancellationToken: cancellationToken)).Throws<RpcException>();
        _ = await client.ReplyPermissionAsync(
            new ReplyPermissionRequest
            {
                UserSessionId = created.Id,
                PermissionRequestId = pending.Id,
                ChoiceValue = "grant",
            },
            cancellationToken: cancellationToken);
        var missing = await Assert.That(async () => await client.ReplyPermissionAsync(
            new ReplyPermissionRequest
            {
                UserSessionId = created.Id,
                PermissionRequestId = pending.Id,
                ChoiceValue = "reject",
            },
            cancellationToken: cancellationToken)).Throws<RpcException>();

        _ = await request;
        _ = await Assert.That(pending.AgentSessionId).IsEqualTo(sessions.Sessions.Single().SessionId);
        _ = await Assert.That(pending.Reason).IsEqualTo("update generated files");
        _ = await Assert.That(pending.Targets[0].Kind).IsEqualTo(PermissionTargetKind.Directory);
        _ = await Assert.That(pending.Targets[0].Scope).IsEqualTo(PermissionTargetScope.Write);
        _ = await Assert.That(pending.Targets[1].Kind).IsEqualTo(PermissionTargetKind.File);
        _ = await Assert.That(pending.Targets[1].Path).IsEqualTo(Path.GetFullPath(file));
        _ = await Assert.That(pending.Choices.Single(choice => choice.Value == "grant").Action)
            .IsEqualTo(PermissionAction.Allow);
        _ = await Assert.That(pending.Choices.Single(choice => choice.Value == "reject").Action)
            .IsEqualTo(PermissionAction.Deny);
        _ = await Assert.That(pending.Choices.Single(choice => choice.RequiresReason).Action)
            .IsEqualTo(PermissionAction.Deny);
        _ = await Assert.That(invalid?.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        _ = await Assert.That(missing?.StatusCode).IsEqualTo(StatusCode.NotFound);
        _ = await Assert.That(sessions.Sessions.Single().WriteGrants.Capture().Targets).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Create_session_defaults_to_noninteractive_permissions(CancellationToken cancellationToken)
    {
        var sessions = new DirectAgentSessions();
        await using var service = Service(Store(sessions));
        var client = new GeneratedParrot.ParrotClient(new InProcessCallInvoker(service));
        var created = await client.CreateSessionAsync(
            new CreateSessionRequest { Model = Selection },
            cancellationToken: cancellationToken);
        _ = await client.SendMessageAsync(
            Send(created.Id, "initialize", "msg-noninteractive"),
            cancellationToken: cancellationToken);
        var target = SandboxWriteTarget.Resolve(EnsureDirectory(Path.Combine(_root, "noninteractive-target")));

        var reply = await sessions.Owners.Single().Permissions.Request(
            sessions.Sessions.Single(),
            "update generated files",
            [target],
            cancellationToken);
        var listed = await client.ListPendingPermissionsAsync(
            new ListPendingPermissionsRequest { UserSessionId = created.Id },
            cancellationToken: cancellationToken);

        _ = await Assert.That(reply.Decision).IsEqualTo(PermissionDecision.Reject);
        _ = await Assert.That(listed.Permissions).IsEmpty();
        _ = await Assert.That(sessions.Sessions.Single().WriteGrants.Capture().Targets).IsEmpty();
    }

    [Test]
    public async Task Model_variants_are_listed_selected_and_unlisted_models_are_persisted(
        CancellationToken cancellationToken)
    {
        var store = Store();
        await using var service = Service(store);
        var context = new InProcessServerCallContext(cancellationToken);

        var listed = await service.ListModels(new ListModelsRequest(), context);
        var created = await service.CreateSession(
            new CreateSessionRequest { Model = "scripted/vendor/model/high" }, context);
        var updated = await service.UpdateSession(
            new UpdateSessionRequest { UserSessionId = created.Id, Model = Selection }, context);
        var unlisted = await service.UpdateSession(
            new UpdateSessionRequest { UserSessionId = created.Id, Model = "scripted/model/missing" }, context);
        var meta = FindMeta(created.Id);

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
        var store = Store();
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
        var meta = FindMeta(session.Id);
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
    public async Task Provider_model_alias_defaults_are_listed_applied_and_reject_unknown_providers(
        CancellationToken cancellationToken)
    {
        var store = Store();
        await using var service = Service(store);
        var client = new GeneratedParrot.ParrotClient(new InProcessCallInvoker(service));

        var listed = await client.ListProviderModelAliasDefaultsAsync(
            new ListProviderModelAliasDefaultsRequest(), cancellationToken: cancellationToken);
        var applied = await client.ApplyProviderModelAliasDefaultsAsync(
            new ApplyProviderModelAliasDefaultsRequest { ProviderId = "scripted" },
            cancellationToken: cancellationToken);
        var aliases = await client.ListModelAliasesAsync(
            new ListModelAliasesRequest(), cancellationToken: cancellationToken);
        var refused = await Assert.That(async () => await client.ApplyProviderModelAliasDefaultsAsync(
            new ApplyProviderModelAliasDefaultsRequest { ProviderId = "missing" },
            cancellationToken: cancellationToken)).Throws<RpcException>();

        _ = await Assert.That(string.Join(",", listed.Providers.Select(provider => provider.ProviderId)))
            .IsEqualTo("scripted");
        _ = await Assert.That(string.Join(",", applied.Aliases.Select(alias => $"{alias.Name}={alias.ModelString}")))
            .IsEqualTo("high_llm=scripted/high,low_llm=scripted/low,medium_llm=scripted/medium,xhigh_llm=scripted/xhigh");
        _ = await Assert.That(aliases.Aliases.Single(alias => alias.Name == "medium_llm").ModelString)
            .IsEqualTo("scripted/medium");
        _ = await Assert.That(refused?.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        var persisted = await File.ReadAllTextAsync(Path.Combine(_root, "config.yaml"), cancellationToken);
        _ = await Assert.That(persisted).Contains("model_string: scripted/low");
        _ = await Assert.That(persisted).Contains("model_string: scripted/xhigh");
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
                alias.Value.AugmentSystemPrompt,
                alias.Value.Icon is null ? null : ModelAliasIcon.Parse(alias.Value.Icon.Glyph, alias.Value.Icon.Color))));
        var configurator = new ModelAliasConfigurator(configuration, catalog);

        _ = await Assert.That(() => configurator.Configure("high_llm", Selection)).Throws<IOException>();
        _ = await Assert.That(catalog.Capture().Find("high_llm")?.ModelString).IsEmpty();
    }

    [Test]
    public async Task A_prompt_with_no_delivery_is_refused(CancellationToken cancellationToken)
    {
        var store = Store();
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
        var store = Store();
        await using var service = Service(store);
        var context = new InProcessServerCallContext(cancellationToken);

        var prompted = await Assert.That(async () =>
            await service.SendMessage(Send("no-such-session", "hello", "msg-1"), context)).Throws<RpcException>();

        var interrupted = await Assert.That(async () => await service.Interrupt(
            new InterruptRequest { UserSessionId = "no-such-session" }, context)).Throws<RpcException>();

        _ = await Assert.That(prompted?.StatusCode).IsEqualTo(StatusCode.NotFound);
        _ = await Assert.That(interrupted?.StatusCode).IsEqualTo(StatusCode.NotFound);
    }

    [Test]
    public async Task Shutdown_is_idempotent_and_refuses_new_session_operations(CancellationToken cancellationToken)
    {
        var store = Store();
        var service = Service(store);
        var context = new InProcessServerCallContext(cancellationToken);
        var session = await service.CreateSession(new CreateSessionRequest { Model = Selection }, context);

        var firstShutdown = service.DisposeAsync().AsTask();
        var secondShutdown = service.DisposeAsync().AsTask();
        await Task.WhenAll(firstShutdown, secondShutdown);

        var create = await Assert.That(async () => await service.CreateSession(
            new CreateSessionRequest { Model = Selection }, context)).Throws<RpcException>();
        var send = await Assert.That(async () => await service.SendMessage(
            Send(session.Id, "too late", "msg-after-shutdown"), context)).Throws<RpcException>();

        _ = await Assert.That(create?.StatusCode).IsEqualTo(StatusCode.Unavailable);
        _ = await Assert.That(send?.StatusCode).IsEqualTo(StatusCode.Unavailable);
    }

    [Test]
    public async Task Listener_receives_the_current_queue_inventory_first(CancellationToken cancellationToken)
    {
        var store = Store();
        await using var service = Service(store);
        var context = new InProcessServerCallContext(cancellationToken);
        var session = await service.CreateSession(new CreateSessionRequest { Model = Selection }, context);
        var resources = new UserSessionResources(
            new StatePaths(_root, _root, _root),
            UserSessionId.Parse(session.Id),
            ProjectWorkspace.FromLaunchDirectory(Path.Combine(_root, "work")));
        using (var queues = new QueueStore(resources.QueueDirectory))
        {
            _ = queues.Create("release", "release tasks");
            _ = queues.Push("release", ["one", "two"], QueueDirection.Back);
        }

        var stream = new ChannelStreamWriter<Event>();
        var listening = service.Listen(new ListenRequest { UserSessionId = session.Id }, stream, context);

        _ = await Assert.That(await stream.Reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(stream.Reader.Current.PayloadCase).IsEqualTo(Event.PayloadOneofCase.QueueSnapshot);
        _ = await Assert.That(stream.Reader.Current.QueueSnapshot.Queues).HasSingleItem();
        _ = await Assert.That(stream.Reader.Current.QueueSnapshot.Queues[0].Name).IsEqualTo("release");
        _ = await Assert.That(stream.Reader.Current.QueueSnapshot.Queues[0].ItemCount).IsEqualTo(2);

        await service.DisposeAsync();
        await listening;
    }

    [Test]
    public async Task Shutdown_completes_an_existing_listener(CancellationToken cancellationToken)
    {
        var store = Store();
        var service = Service(store);
        var context = new InProcessServerCallContext(cancellationToken);
        var session = await service.CreateSession(new CreateSessionRequest { Model = Selection }, context);
        var stream = new ChannelStreamWriter<Event>();
        var listen = service.Listen(new ListenRequest { UserSessionId = session.Id }, stream, context);

        await service.DisposeAsync();
        await listen;
    }

    [Test]
    public async Task Shutdown_closes_admission_without_waiting_to_acquire_a_slow_open_gate(
        CancellationToken cancellationToken)
    {
        var store = Store();
        await using var registry = new UserSessionRegistry();
        using var entered = new SemaphoreSlim(0, 1);
        using var release = new ManualResetEventSlim();
        var opening = Task.Run(
            async () => await registry.Host(() =>
            {
                _ = entered.Release();
                release.Wait(cancellationToken);
                return store.Open(_router.Resolve(Selection));
            }),
            cancellationToken);
        await entered.WaitAsync(cancellationToken);

        var shutdown = registry.DisposeAsync().AsTask();
        var refused = await Assert.That(() => registry.Find("missing")).Throws<RpcException>();

        _ = await Assert.That(shutdown.IsCompleted).IsFalse();
        _ = await Assert.That(refused?.StatusCode).IsEqualTo(StatusCode.Unavailable);

        release.Set();
        RpcException? creation = null;
        try
        {
            _ = await opening;
        }
        catch (RpcException failure)
        {
            creation = failure;
        }

        await shutdown;
        _ = await Assert.That(creation).IsNotNull();
        _ = await Assert.That(creation?.StatusCode).IsEqualTo(StatusCode.Unavailable);
    }

    [Test]
    public async Task Shutdown_disposes_a_session_registered_immediately_before_it_and_stays_closed(
        CancellationToken cancellationToken)
    {
        var store = Store();
        var service = Service(store);
        var context = new InProcessServerCallContext(cancellationToken);

        var create = service.CreateSession(new CreateSessionRequest { Model = Selection }, context);
        var shutdown = service.DisposeAsync().AsTask();
        _ = await create;
        await shutdown;

        var refused = await Assert.That(async () => await service.CreateSession(
            new CreateSessionRequest { Model = Selection }, context)).Throws<RpcException>();
        _ = await Assert.That(refused?.StatusCode).IsEqualTo(StatusCode.Unavailable);
    }

    private static async Task WaitForPermission(
        Agent.UserSession session,
        CancellationToken cancellationToken)
    {
        while (session.Permissions.Pending().Count == 0)
        {
            await Task.Delay(10, cancellationToken);
        }
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

    private static string EnsureDirectory(string path)
    {
        _ = Directory.CreateDirectory(path);
        return path;
    }

    private void PublishMeta(
        string id,
        string rootAgentName,
        string model,
        string mode,
        string createdAt)
    {
        var resources = new UserSessionResources(
            new StatePaths(_root, _root, _root),
            UserSessionId.Parse(id),
            ProjectWorkspace.FromLaunchDirectory(EnsureDirectory(Path.Combine(_root, "inactive-work"))));
        new SessionIndex(resources).Publish(new SessionMeta
        {
            Id = id,
            WorkingDirectory = resources.Workspace.LaunchDirectory,
            RootAgentName = rootAgentName,
            ProviderId = "scripted",
            Model = model,
            Mode = mode,
            CreatedAt = createdAt,
        });
    }

    private SessionMeta FindMeta(string id)
    {
        var resources = new UserSessionResources(
            new StatePaths(_root, _root, _root),
            UserSessionId.Parse(id),
            ProjectWorkspace.FromLaunchDirectory(Path.Combine(_root, "work")));
        return new SessionIndex(resources).Find()
            ?? throw new InvalidOperationException($"Session metadata not found for '{id}'.");
    }

    private ParrotService Service(SessionStore store) => new(
        _router,
        _registry,
        new ModelAliasConfigurator(_configuration, _catalog),
        store,
        new SessionCatalog(new StatePaths(_root, _root, _root)),
        Modes());

    private ModeRegistry Modes() => new(
        new ProfileRegistry(
            _configuration.Profiles,
            _configuration.SandboxRules,
            [],
            _configuration.DisabledTools),
        _configuration.DefaultProfile);

    private SessionStore Store() => Store(new DirectAgentSessions());

    private SessionStore Store(DirectAgentSessions sessions)
    {
        sessions.Use(_router);
        return new(
            new StatePaths(_root, _root, _root),
            EnsureDirectory(Path.Combine(_root, "work")),
            "host",
            new UserSessionFactory(sessions, Modes(), TestModels.ProfileRegistry(), TimeSpan.FromSeconds(30), TimeProvider.System),
            _router,
            Modes());
    }
}
