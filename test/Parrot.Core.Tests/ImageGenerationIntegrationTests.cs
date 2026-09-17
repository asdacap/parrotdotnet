using System.Net;
using System.Text;
using System.Text.Json;
using Parrot.Agent;
using Parrot.Auth;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Llm.Wire;
using Parrot.Security;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class ImageGenerationIntegrationTests : IDisposable
{
    private const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==";
    private const string Prompt = "  Paint a café.\nKeep the layout.  ";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-imagegen-integration-tests", Guid.NewGuid().ToString("n"));

    public ImageGenerationIntegrationTests() => _ = Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Test]
    [Arguments(false, "generate")]
    [Arguments(true, "generate")]
    [Arguments(false, "overwrite")]
    [Arguments(true, "overwrite")]
    [Arguments(false, "edit")]
    [Arguments(true, "edit")]
    [Arguments(false, "http-failure")]
    [Arguments(true, "http-failure")]
    [Arguments(false, "invalid-base64")]
    [Arguments(true, "invalid-base64")]
    public async Task Factory_tool_uses_current_provider_http_and_security_aware_persistence(
        bool oauth, string scenario, CancellationToken cancellationToken)
    {
        var existing = scenario != "generate";
        var edit = scenario == "edit";
        var failure = scenario is "http-failure" or "invalid-base64";
        var outputPath = Path.Combine(_root, "output.png");
        byte[] original = [.. Convert.FromBase64String(PngBase64), .. new byte[200]];
        if (existing)
        {
            await File.WriteAllBytesAsync(outputPath, original, cancellationToken);
        }

        using var handler = new ImageHandler(outputPath, original, existing, edit, oauth, scenario);
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = oauth
            ? new ChatGptProvider(new OAuthTokens(), client, [], [], [], true, new ResponsesWebSocketConnector())
            : new RetryingProvider(new OpenAICompatibleProvider(
                new OpenAICompatibleOptions
                {
                    Id = "configured-images",
                    BaseUrl = "https://images.example.test/custom/v1/",
                    ApiKeySource = new ImageApiKeySource(),
                    Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Tenant"] = "test-tenant" },
                },
                client));
        var model = new ProviderModel(provider, new LLMModel("text-model", provider.Id));
        var identity = AgentIdentity.Main("session", string.Empty, TestModels.PromptTemplates);
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        using var dependencies = TestModels.Dependencies(identity, events, repository, cancellationToken);
        var securityProfile = SecurityProfile.Compose(true, [], [], [new SandboxRule(_root, SandboxRuleAction.AllowWrite)]);
        await using IAgentSession session = new AgentSession(identity, AgentSessionParentScope.Root(), new ModelSelector(model.Selector), TestModels.Route(model), events, repository, [], TestModels.MaterializePrompt(identity, _root, _root), new ToolOutputBlobStore(Path.Combine(_root, "blobs")), new AgentOutputFile(Path.Combine(_root, "blobs")), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null), new ContextCadence(), TestModels.PromptTemplates, dependencies.ChildQuestions, dependencies.ExitReminder, dependencies.Profile, new TestCompletionCallbacksFixture(dependencies.ChildQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, repository, events).Callbacks, new SecurityProfileTestFixture(securityProfile).Security, dependencies.Status, new AgentSessionActivity(TimeProvider.System), TestDiagnosticLog.Instance, cancellationToken);
        IToolFactory factory = new ImageGenerationToolFactory(new ToolWorkspace(_root), TestModels.ToolDefinitions);
        var snapshot = ToolSnapshot.Document([factory.Create(session)], [factory.Supports(session)], [factory.Definition]);
        var tool = snapshot.Find("imagegen") ?? throw new InvalidOperationException("Image tool was not registered.");
        var selection = new AgentTurnSelection(new ModelSelector(model.Selector), TestModels.Resolve(model), new TestProfileFixture().Mode, securityProfile);
        var arguments = $$"""{"prompt":"{{JsonEncodedText.Encode(Prompt)}}","output_path":"output.png","referenced_image_paths":{{(edit ? "[\"output.png\"]" : "[]")}}}""";
        var invocation = new ToolInvocation("image-call", arguments) { PromptTemplates = TestModels.PromptTemplates };

        var result = await tool.Execute(invocation, selection, cancellationToken);

        _ = await Assert.That(handler.Calls).IsEqualTo(1);
        _ = await Assert.That(result.ImageArtifacts).IsEmpty();
        _ = await Assert.That(result.Text.StartsWith("error: ", StringComparison.Ordinal)).IsEqualTo(failure);
        if (!failure)
        {
            _ = await Assert.That(result.Text).IsEqualTo(ToolResultFormatter.Text(invocation, outputPath));
        }

        var saved = await File.ReadAllBytesAsync(outputPath, cancellationToken);
        _ = await Assert.That(saved.SequenceEqual(failure ? original : Convert.FromBase64String(PngBase64))).IsTrue();
    }

    private sealed class ImageApiKeySource : IApiKeySource
    {
        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<string> ApiKey(CancellationToken cancellationToken) => ValueTask.FromResult("offline-api-key");
    }

    private sealed class OAuthTokens : IOAuthTokenSource
    {
        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<OAuthAccess> Token(CancellationToken cancellationToken) => Task.FromResult(new OAuthAccess("offline-oauth-token", "offline-account"));
    }

    private sealed class ImageHandler(string outputPath, byte[] original, bool existing, bool edit, bool oauth, string scenario) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            _ = await Assert.That(File.Exists(outputPath)).IsEqualTo(existing);
            if (existing)
            {
                var beforeResponse = await File.ReadAllBytesAsync(outputPath, cancellationToken);
                _ = await Assert.That(beforeResponse.SequenceEqual(original)).IsTrue();
            }

            var endpoint = oauth ? "https://chatgpt.com/backend-api/codex" : "https://images.example.test/custom/v1";
            _ = await Assert.That(request.RequestUri?.AbsoluteUri).IsEqualTo(endpoint + (edit ? "/images/edits" : "/images/generations"));
            _ = await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
            _ = await Assert.That(request.Headers.Authorization?.ToString()).IsEqualTo(oauth ? "Bearer offline-oauth-token" : "Bearer offline-api-key");
            if (oauth)
            {
                _ = await Assert.That(request.Headers.GetValues("ChatGPT-Account-Id").Single()).IsEqualTo("offline-account");
                _ = await Assert.That(request.Headers.GetValues("originator").Single()).IsEqualTo("parrot");
            }
            else
            {
                _ = await Assert.That(request.Headers.GetValues("X-Tenant").Single()).IsEqualTo("test-tenant");
            }

            var content = request.Content ?? throw new InvalidOperationException("Image request has no content.");
            _ = await Assert.That(content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
            using var body = JsonDocument.Parse(await content.ReadAsStringAsync(cancellationToken));
            _ = await Assert.That(body.RootElement.GetProperty("prompt").GetString()).IsEqualTo(Prompt);
            _ = await Assert.That(body.RootElement.GetProperty("model").GetString()).IsEqualTo("gpt-image-2");
            _ = await Assert.That(body.RootElement.TryGetProperty("images", out var images)).IsEqualTo(edit);
            if (edit)
            {
                _ = await Assert.That(images.GetArrayLength()).IsEqualTo(1);
                _ = await Assert.That(images[0].GetProperty("image_url").GetString()).IsEqualTo("data:image/png;base64," + Convert.ToBase64String(original));
            }

            return new HttpResponseMessage(scenario == "http-failure" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"b64_json\":\"" + (scenario == "invalid-base64" ? "%%%" : PngBase64) + "\"}]}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
