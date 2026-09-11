using System.Net;
using System.Text;
using Parrot.Agent;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Security;
using Parrot.State;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Cli.Tests;

internal sealed class ProviderRequestIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-request-integration", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Real_provider_headers_clear_requesting_modeline_before_response_body(
        CancellationToken cancellationToken)
    {
        var configuration = Configuration.Load(Path.Combine(_root, "config.yaml"), Path.Combine(_root, "predefined_config.yaml"));
        using var diagnostics = new TransportDiagnosticsFixture();
        using var body = new BodyBarrierStream();
        using var handler = new HeaderBarrierHandler(body);
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "provider",
                BaseUrl = "https://provider.invalid/v1",
                ApiKeySource = new FixedApiKeySource(),
            },
            client);
        var providers = new ProviderRegistry([provider], new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
        {
            [provider.Id] = [new LLMModel("model", provider.Id)],
        });
        var router = new ModelRouter(providers, new ModelRouting(new ModelAliasCatalog(providers, []), "provider/model"));
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        using var broker = new EventBroker();
        using var subscription = broker.Subscribe();
        var templates = configuration.PromptTemplates;
        var identity = AgentIdentity.Main("root", string.Empty, templates);
        var profiles = new ProfileRegistry(configuration.Profiles, [], [], new HashSet<string>(StringComparer.Ordinal));
        var profile = profiles.Resolve("build");
        IMode mode = new NoopMode(profile, profile.SecurityProfile);
        await using IAgentRegistry registry = new AgentRegistry(
            new UnsupportedAgentSessionFactory(), broker, repository, profiles, templates, new RetainedAgentBudget(1), diagnostics.Log, cancellationToken);
        var status = new RuntimeStatus(registry, templates, TimeProvider.System);
        registry.AttachStatus(status);
        var resources = new UserSessionResources(new StatePaths(_root, _root, _root), UserSessionId.Parse("request-integration"), ProjectWorkspace.FromLaunchDirectory(_root));
        await using var children = new ChildRegistry(identity);
        using var queues = new AgentQueues(identity, null, resources, children, diagnostics.Log);
        queues.Initialize();
        var questions = new ChildQuestionCoordinator(AgentSessionParentScope.Root(), templates);
        await using IAgentSession agent = new AgentSession(
            identity, AgentSessionParentScope.Root(), new ModelSelector("provider/model"), router, broker, repository, [], new ToolDefinitionCatalog(new Dictionary<string, ConfiguredToolDefinition>(StringComparer.Ordinal)), new ConfiguredSystemPromptProvider("test:integration", "Reply briefly.").Materialize(identity), new ToolOutputBlobStore(_root), new CompactionGroupBlobStore(resources.AgentScratch(identity.SessionId)), new Compactor(int.MaxValue, 30, 60_000, 1024, templates), new ProviderSessions(diagnostics.Log, identity.SessionId), new ContextCadence(), templates, questions, new ExitReminder(repository, templates, identity.SessionId), mode, [], new AgentSessionSecurity(profile.SecurityProfile, resources.Workspace, resources.ScratchRootDirectory), status, queues, new AgentSessionActivity(TimeProvider.System), diagnostics.Log, cancellationToken);
        queues.Attach(agent);

        using var output = new StringWriter();
        using var error = new StringWriter();
        using var input = new ScriptedInput();
        var terminal = new TestTerminal(input, output, error, 160);
        var presenters = new ToolPresenterRegistry([], new GenericToolPresenter());
        var requesting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var headersReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseText = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new ChannelStreamWriter<Event>();
        await using var rendering = new EnhancedRenderingSession(
            new EnhancedTurnRenderer(terminal, configuration, presenters),
            presenters,
            new TerminalFrameRenderer(output, terminal.GetColumns, new TerminalPalette(false), 10, 12, true),
            new TestSlashSession("provider/model"),
            [new PromptValue("> ", "draft", 0)],
            (published, token) =>
            {
                if (published.PayloadCase == Event.PayloadOneofCase.ProviderRequestPhaseChanged)
                {
                    if (published.ProviderRequestPhaseChanged.Phase == ProviderRequestPhase.Requesting)
                    {
                        _ = requesting.TrySetResult();
                    }
                    else if (published.ProviderRequestPhaseChanged.Phase == ProviderRequestPhase.HeadersReceived)
                    {
                        _ = headersReceived.TrySetResult();
                    }
                }
                else if (published.PayloadCase == Event.PayloadOneofCase.TextChunk)
                {
                    _ = responseText.TrySetResult();
                }

                return Task.CompletedTask;
            },
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static () => true,
            static (_, _) => Task.CompletedTask,
            true);
        var forwarding = ForwardEvents();
        var displaying = rendering.Run(stream.Reader, cancellationToken);
        try
        {
            _ = await agent.Send([ConversationPart.TextPart("hello")], "message", Delivery.Steer, cancellationToken);
            await handler.RequestEntered.Task.WaitAsync(cancellationToken);
            await requesting.Task.WaitAsync(cancellationToken);
            var beforeHeaders = output.GetStringBuilder().Length;
            await rendering.Refresh(cancellationToken);
            var requestingFrame = output.ToString()[beforeHeaders..];
            _ = await Assert.That(requestingFrame).Contains("Requesting…");
            _ = await Assert.That(requestingFrame).Contains("provider/model");
            _ = await Assert.That(requestingFrame).Contains("draft");
            _ = await Assert.That(headersReceived.Task.IsCompleted).IsFalse();
            _ = await Assert.That(body.ReadEntered.Task.IsCompleted).IsFalse();
            _ = await Assert.That(responseText.Task.IsCompleted).IsFalse();

            handler.ReleaseHeaders.SetResult();
            await headersReceived.Task.WaitAsync(cancellationToken);
            await body.ReadEntered.Task.WaitAsync(cancellationToken);
            var afterHeaders = output.GetStringBuilder().Length;
            await rendering.Refresh(cancellationToken);
            var headersFrame = output.ToString()[afterHeaders..];
            _ = await Assert.That(headersFrame).DoesNotContain("Requesting");
            _ = await Assert.That(headersFrame).Contains("provider/model");
            _ = await Assert.That(headersFrame).Contains("agent main (running");
            _ = await Assert.That(responseText.Task.IsCompleted).IsFalse();
            _ = await Assert.That(output.ToString()).DoesNotContain("body sentinel");

            body.ReleaseBody.SetResult();
            await agent.Settled();
            await forwarding.WaitAsync(cancellationToken);
            var completed = await displaying.WaitAsync(cancellationToken);
            _ = await Assert.That(error.ToString()).IsEmpty();
            _ = await Assert.That(completed).IsTrue();
            _ = await Assert.That(responseText.Task.IsCompleted).IsTrue();
            _ = await Assert.That(output.ToString()).Contains("body sentinel");
        }
        finally
        {
            _ = handler.ReleaseHeaders.TrySetResult();
            _ = body.ReleaseBody.TrySetResult();
            await agent.Settled();
            subscription.Dispose();
            await forwarding;
            _ = await displaying;
            questions.Dispose();
        }

        async Task ForwardEvents()
        {
            try
            {
                await foreach (var published in subscription.Reader.ReadAllAsync(cancellationToken))
                {
                    await stream.WriteAsync(published, cancellationToken);
                    if (published.PayloadCase is Event.PayloadOneofCase.TurnEnded or Event.PayloadOneofCase.TurnFailed)
                    {
                        break;
                    }
                }
            }
            finally
            {
                stream.Complete();
            }
        }
    }

    private sealed class HeaderBarrierHandler(Stream body) : HttpMessageHandler
    {
        public TaskCompletionSource RequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseHeaders { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestEntered.SetResult();
            await ReleaseHeaders.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        }
    }

    private sealed class BodyBarrierStream() : MemoryStream(Encoding.UTF8.GetBytes(
        "data: {\"choices\":[{\"delta\":{\"content\":\"body sentinel\"},\"finish_reason\":null}]}\n\n"
        + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n"))
    {
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseBody { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            _ = ReadEntered.TrySetResult();
            await ReleaseBody.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class FixedApiKeySource : IApiKeySource
    {
        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<string> ApiKey(CancellationToken cancellationToken) => ValueTask.FromResult("test-key");
    }

    private sealed class UnsupportedAgentSessionFactory : IAgentSessionFactory
    {
        public EventRepository PrepareHistory(string agentSessionId, EventRepository repository) =>
            throw new NotSupportedException("This test does not spawn agents.");

        public IAgentSessionScope Create(
            AgentIdentity identity, AgentSessionParentLink parentLink, ModelSelector model, EventBroker eventBroker, EventRepository eventRepository, IMode mode, SecurityProfile securityProfile, RuntimeStatus status, IAgentRegistry registry, CancellationToken lifetime) =>
            throw new NotSupportedException("This test does not spawn agents.");
    }
}
