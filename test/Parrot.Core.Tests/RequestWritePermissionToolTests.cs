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
            TimeSpan.FromSeconds(30));
        var profile = SecurityProfile.Compose(readOnly: false, [], [], []);
        var tool = new RequestWritePermissionTool(broker, Session(database, events, profile), profile);
        var path = Path.Combine(_root, "dependency");
        await File.WriteAllTextAsync(path, "content", cancellationToken);

        var encodedPath = Encode(path);
        var result = await tool.Execute(
            $$"""{"paths":["{{encodedPath}}"],"reason":"update dependency"}""",
            cancellationToken);
        var missingReason = await tool.Execute($$"""{"paths":["{{encodedPath}}"]}""", cancellationToken);
        var missingPath = await tool.Execute(
            $$"""{"paths":["{{Encode(Path.Combine(_root, "missing"))}}"],"reason":"update"}""",
            cancellationToken);
        var unexpected = await tool.Execute(
            $$"""{"paths":["{{encodedPath}}"],"reason":"update","extra":true}""",
            cancellationToken);

        _ = await Assert.That(result).IsEqualTo("Write permission request rejected.");
        _ = await Assert.That(missingReason).StartsWith("error:");
        _ = await Assert.That(missingPath).StartsWith("error:");
        _ = await Assert.That(unexpected).StartsWith("error:");
        _ = await Assert.That(broker.Pending()).IsEmpty();
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
            TimeSpan.FromSeconds(30));
        var profile = SecurityProfile.Compose(readOnly: true, [], [], []);
        var tool = new RequestWritePermissionTool(broker, Session(database, events, profile), profile);
        var path = Path.Combine(_root, "dependency");
        await File.WriteAllTextAsync(path, "content", cancellationToken);

        var result = await tool.Execute(
            $$"""{"paths":["{{Encode(path)}}"],"reason":"update dependency"}""",
            cancellationToken);

        _ = await Assert.That(result).Contains("not permitted by the current security profile");
        _ = await Assert.That(broker.Pending()).IsEmpty();
    }

    private static string Encode(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static AgentSession Session(
        SessionDatabase database,
        EventBroker events,
        SecurityProfile securityProfile)
    {
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var repository = new EventRepository(database);
        return new AgentSession(
            AgentIdentity.Main("requesting", string.Empty),
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            events,
            repository,
            [],
            TestModels.PromptProvider(".", "."),
            new TodoCollection("requesting", repository, events),
            new ToolOutputBlobStore(Path.GetTempPath()),
            new Compactor(120_000),
            null,
            securityProfile,
            null,
            null,
            null,
            CancellationToken.None);
    }
}
