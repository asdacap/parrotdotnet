using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Permissions;
using Parrot.Security;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class RequestWritePermissionToolTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "parrot-request-write-permission-tests",
        Guid.NewGuid().ToString("n"));

    public RequestWritePermissionToolTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Test]
    public async Task Exact_paths_and_reason_are_required_and_noninteractive_rejection_is_useful(
        CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var events = new EventBroker();
        using var broker = new PermissionBroker(
            events,
            new EventRepository(database),
            interactive: false,
            TimeSpan.FromSeconds(30),
            TimeProvider.System,
            TestDiagnosticLog.Instance);
        var profile = SecurityProfile.Compose(
            readOnly: false,
            [new SandboxRule(_root, SandboxRuleAction.DenyWrite)],
            [],
            []);
        ITool tool = new RequestWritePermissionTool(
            AgentIdentity.Main("requesting", string.Empty, TestModels.PromptTemplates),
            new SecurityProfileTestFixture(profile).Security,
            broker);
        var path = Path.Combine(_root, "dependency");
        await File.WriteAllTextAsync(path, "content", cancellationToken);

        var encodedPath = Encode(path);
        var result = (await tool.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"paths":["{{encodedPath}}"],"reason":"update dependency"}"""),
            new SelectionFixture(profile).Selection,
            cancellationToken)).Text;
        var missingReason = (await tool.Execute(new ToolInvocation("test-call", $$"""{"paths":["{{encodedPath}}"]}"""), new SelectionFixture(profile).Selection, cancellationToken)).Text;
        var missingPath = (await tool.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"paths":["{{Encode(Path.Combine(_root, "missing"))}}"],"reason":"update"}"""),
            new SelectionFixture(profile).Selection,
            cancellationToken)).Text;
        var unexpected = (await tool.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"paths":["{{encodedPath}}"],"reason":"update","extra":true}"""),
            new SelectionFixture(profile).Selection,
            cancellationToken)).Text;

        _ = await Assert.That(result).IsEqualTo("Write permission request rejected.");
        _ = await Assert.That(missingReason).StartsWith("error:");
        _ = await Assert.That(missingPath).StartsWith("error:");
        _ = await Assert.That(unexpected).StartsWith("error:");
        _ = await Assert.That(broker.Pending()).IsEmpty();
    }

    [Test]
    public async Task Profile_authorized_paths_complete_without_a_permission_request(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var events = new EventBroker();
        using var broker = new PermissionBroker(
            events,
            new EventRepository(database),
            interactive: true,
            TimeSpan.FromMinutes(20),
            TimeProvider.System,
            TestDiagnosticLog.Instance);
        var path = Path.Combine(_root, "profile-authorized");
        await File.WriteAllTextAsync(path, "content", cancellationToken);
        var profile = SecurityProfile.Compose(
            readOnly: false,
            [
                new SandboxRule(_root, SandboxRuleAction.DenyWrite),
                new SandboxRule(path, SandboxRuleAction.AllowWrite),
            ],
            [],
            []);
        var security = new SecurityProfileTestFixture(profile).Security;
        await using var session = Session(database, events, security);
        ITool tool = new RequestWritePermissionTool(
            AgentIdentity.Main("requesting", string.Empty, TestModels.PromptTemplates), security, broker);

        var result = (await tool.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"paths":["{{Encode(path)}}"],"reason":"update dependency"}"""),
            new SelectionFixture(profile).Selection,
            cancellationToken)).Text;

        _ = await Assert.That(result).Contains("already allowed by the current security profile");
        _ = await Assert.That(broker.Pending()).IsEmpty();
        _ = await Assert.That(security.Capture(profile).AllowsWrite(path)).IsTrue();
    }

    [Test]
    public async Task Timeout_returns_user_away_as_a_normal_result(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var events = new EventBroker();
        var time = new ControlledTimeProvider();
        using var broker = new PermissionBroker(
            events,
            new EventRepository(database),
            interactive: true,
            TimeSpan.FromMinutes(20),
            time,
            TestDiagnosticLog.Instance);
        var profile = SecurityProfile.Compose(
            readOnly: false,
            [new SandboxRule(_root, SandboxRuleAction.DenyWrite)],
            [],
            []);
        ITool tool = new RequestWritePermissionTool(
            AgentIdentity.Main("requesting", string.Empty, TestModels.PromptTemplates),
            new SecurityProfileTestFixture(profile).Security,
            broker);
        var path = Path.Combine(_root, "dependency");
        await File.WriteAllTextAsync(path, "content", cancellationToken);
        var executing = tool.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"paths":["{{Encode(path)}}"],"reason":"update dependency"}"""),
            new SelectionFixture(profile).Selection,
            cancellationToken);
        _ = await WaitForPending(broker, cancellationToken);
        await time.WaitForTimer(cancellationToken);

        time.Advance(TimeSpan.FromMinutes(20));

        _ = await Assert.That((await executing).Text).IsEqualTo("The user is away.");
    }

    [Test]
    public async Task Read_only_profile_rejects_before_a_pending_request_is_created(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var events = new EventBroker();
        using var broker = new PermissionBroker(
            events,
            new EventRepository(database),
            interactive: true,
            TimeSpan.FromSeconds(30),
            TimeProvider.System,
            TestDiagnosticLog.Instance);
        var profile = SecurityProfile.Compose(readOnly: true, [], [], []);
        ITool tool = new RequestWritePermissionTool(
            AgentIdentity.Main("requesting", string.Empty, TestModels.PromptTemplates),
            new SecurityProfileTestFixture(profile).Security,
            broker);
        var path = Path.Combine(_root, "dependency");
        await File.WriteAllTextAsync(path, "content", cancellationToken);

        var result = (await tool.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"paths":["{{Encode(path)}}"],"reason":"update dependency"}"""),
            new SelectionFixture(profile).Selection,
            cancellationToken)).Text;

        _ = await Assert.That(result).Contains("not permitted by the current security profile");
        _ = await Assert.That(broker.Pending()).IsEmpty();
    }

    private static string Encode(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static async Task<PermissionPending> WaitForPending(
        PermissionBroker broker,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var pending = broker.Pending();
            if (pending.Count == 1)
            {
                return pending[0];
            }

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IAgentSession Session(
        SessionDatabase database,
        IEventBroker events,
        AgentSessionSecurity security)
    {
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var repository = new EventRepository(database);
        var identity = AgentIdentity.Main("requesting", string.Empty, TestModels.PromptTemplates);
        using var dependencies = TestModels.Dependencies(identity, events, repository, CancellationToken.None);
        return new AgentSession(identity, AgentSessionParentScope.Root(), new ModelSelector(model.Selector), TestModels.Route(model), events, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(Path.GetTempPath()), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null), new ContextCadence(), TestModels.PromptTemplates, dependencies.ChildQuestions, dependencies.ExitReminder, dependencies.Profile, new TestCompletionCallbacksFixture(dependencies.ChildQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, repository, events).Callbacks, security, dependencies.Status, dependencies.Queues, new AgentSessionActivity(TimeProvider.System), TestDiagnosticLog.Instance, CancellationToken.None);
    }

    private sealed class SelectionFixture
    {
        public SelectionFixture(SecurityProfile profile)
        {
            ILLMProvider provider = new UnusedProvider();
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
            Selection = new AgentTurnSelection(
                new ModelSelector(model.Selector),
                TestModels.Resolve(model),
                new TestProfileFixture().Mode,
                profile);
        }

        public AgentTurnSelection Selection { get; }
    }
}
