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
            TimeProvider.System);
        var profile = SecurityProfile.Compose(
            readOnly: false,
            [new SandboxRule(_root, SandboxRuleAction.DenyWrite)],
            [],
            []);
        var tool = new RequestWritePermissionTool(broker, Session(database, events, profile));
        var path = Path.Combine(_root, "dependency");
        await File.WriteAllTextAsync(path, "content", cancellationToken);

        var encodedPath = Encode(path);
        var result = (await tool.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"paths":["{{encodedPath}}"],"reason":"update dependency"}"""),
            Selection(profile),
            cancellationToken)).Text;
        var missingReason = (await tool.Execute(new ToolInvocation("test-call", $$"""{"paths":["{{encodedPath}}"]}"""), Selection(profile), cancellationToken)).Text;
        var missingPath = (await tool.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"paths":["{{Encode(Path.Combine(_root, "missing"))}}"],"reason":"update"}"""),
            Selection(profile),
            cancellationToken)).Text;
        var unexpected = (await tool.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"paths":["{{encodedPath}}"],"reason":"update","extra":true}"""),
            Selection(profile),
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
            TimeProvider.System);
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
        var session = Session(database, events, profile);
        var tool = new RequestWritePermissionTool(broker, session);

        var result = (await tool.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"paths":["{{Encode(path)}}"],"reason":"update dependency"}"""),
            Selection(profile),
            cancellationToken)).Text;

        _ = await Assert.That(result).Contains("already allowed by the current security profile");
        _ = await Assert.That(broker.Pending()).IsEmpty();
        _ = await Assert.That(session.ResolveSelection().SecurityProfile.AllowsWrite(path)).IsTrue();
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
            time);
        var profile = SecurityProfile.Compose(
            readOnly: false,
            [new SandboxRule(_root, SandboxRuleAction.DenyWrite)],
            [],
            []);
        var tool = new RequestWritePermissionTool(broker, Session(database, events, profile));
        var path = Path.Combine(_root, "dependency");
        await File.WriteAllTextAsync(path, "content", cancellationToken);
        var executing = tool.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"paths":["{{Encode(path)}}"],"reason":"update dependency"}"""),
            Selection(profile),
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
            TimeProvider.System);
        var profile = SecurityProfile.Compose(readOnly: true, [], [], []);
        var tool = new RequestWritePermissionTool(broker, Session(database, events, profile));
        var path = Path.Combine(_root, "dependency");
        await File.WriteAllTextAsync(path, "content", cancellationToken);

        var result = (await tool.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"paths":["{{Encode(path)}}"],"reason":"update dependency"}"""),
            Selection(profile),
            cancellationToken)).Text;

        _ = await Assert.That(result).Contains("not permitted by the current security profile");
        _ = await Assert.That(broker.Pending()).IsEmpty();
    }

    private static AgentTurnSelection Selection(SecurityProfile profile)
    {
        var provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        return new AgentTurnSelection(
            new ModelSelector(model.Selector),
            TestModels.Resolve(model),
            TestModels.Profile(),
            profile);
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

    private static AgentSession Session(
        SessionDatabase database,
        EventBroker events,
        SecurityProfile securityProfile)
    {
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var repository = new EventRepository(database);
        var identity = AgentIdentity.Main("requesting", string.Empty);
        var dependencies = TestModels.Dependencies(identity, events, repository, CancellationToken.None);
        return new AgentSession(
            identity,
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            events,
            repository,
            [],
            TestModels.EmptyToolDefinitions,
            TestModels.MaterializePrompt(identity, ".", "."),
            new TodoCollection("requesting", repository, events),
            new ToolOutputBlobStore(Path.GetTempPath()),
            new Compactor(90, 30, 60_000, 1024),
            dependencies.ActiveWorkReminder,
            dependencies.Profile,
            SecurityProfileTestFactory.Create(securityProfile),
            dependencies.Status,
            dependencies.Registry,
            dependencies.Queues,
            CancellationToken.None);
    }
}
