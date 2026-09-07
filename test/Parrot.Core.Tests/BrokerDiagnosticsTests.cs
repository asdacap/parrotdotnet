using Parrot.Agent;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Permissions;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Security;
using Parrot.State;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class BrokerDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-broker-diagnostics-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Test]
    [Arguments("answered")]
    [Arguments("rejected")]
    [Arguments("timeout")]
    [Arguments("cancelled")]
    [Arguments("failed")]
    public async Task Question_wait_records_safe_terminal_outcome(string outcome, CancellationToken cancellationToken)
    {
        var resources = PrepareResources();
        using var diagnostics = FileDiagnosticLog.OpenSession(resources, "test", TextWriter.Null, TimeProvider.System);
        var time = new ControlledTimeProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var broker = new QuestionBroker(TimeSpan.FromMinutes(1), time, diagnostics);
        if (outcome == "failed")
        {
            broker.Dispose();
            _ = await Assert.That(async () => await broker.Ask(
                [new QuestionDefinition("private-sentinel", "private-sentinel", [], false, true)], cancellation.Token))
                .Throws<ObjectDisposedException>();
            var failedLog = await File.ReadAllTextAsync(resources.LogPath, cancellationToken);
            _ = await Assert.That(failedLog).Contains("outcome=\"failed\"").And.DoesNotContain("private-sentinel");
            return;
        }

        var waiting = broker.Ask([new QuestionDefinition("private-sentinel", "private-sentinel", [], false, true)], cancellation.Token);
        var request = broker.Pending().Single();
        switch (outcome)
        {
            case "answered":
                broker.Reply(request.Id, new QuestionReply([new QuestionAnswer("private-sentinel")]));
                _ = await waiting;
                break;
            case "rejected":
                broker.Reject(request.Id);
                _ = await Assert.That(waiting).Throws<QuestionRejectedException>();
                break;
            case "timeout":
                await time.WaitForTimer(cancellationToken);
                time.Advance(TimeSpan.FromMinutes(1));
                _ = await Assert.That((await waiting).Kind).IsEqualTo(QuestionReplyKind.UserAway);
                break;
            case "cancelled":
                await cancellation.CancelAsync();
                _ = await Assert.That(waiting).Throws<OperationCanceledException>();
                break;
        }

        var log = await File.ReadAllTextAsync(resources.LogPath, cancellationToken);
        _ = await Assert.That(log).Contains("event=\"wait_started\"").And.Contains("event=\"wait_completed\"")
            .And.Contains($"outcome=\"{outcome}\"").And.Contains(request.Id).And.DoesNotContain("private-sentinel");
    }

    [Test]
    [Arguments("granted")]
    [Arguments("rejected")]
    [Arguments("timeout")]
    [Arguments("cancelled")]
    [Arguments("failed")]
    public async Task Permission_request_records_safe_decision(string outcome, CancellationToken cancellationToken)
    {
        var resources = PrepareResources();
        using var diagnostics = FileDiagnosticLog.OpenSession(resources, "test", TextWriter.Null, TimeProvider.System);
        using var database = SessionDatabase.Open(":memory:");
        using var events = new EventBroker();
        var time = new ControlledTimeProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var broker = new PermissionBroker(events, new EventRepository(database), true, TimeSpan.FromMinutes(1), time, diagnostics);
        var path = Path.Combine(_root, "private-sentinel");
        await File.WriteAllTextAsync(path, "private-sentinel", cancellationToken);
        var security = new SecurityProfileTestFixture(SecurityProfile.Compose(false, [], [], [])).Security;
        var waiting = broker.Request(AgentIdentity.Main("agent", "main", TestModels.PromptTemplates), security, "private-sentinel", [SecurityWriteTarget.Resolve(path)], cancellation.Token);
        var request = broker.Pending().Single();
        if (outcome == "failed")
        {
            broker.Dispose();
            _ = await Assert.That(waiting).Throws<PermissionException>();
        }
        else if (outcome == "cancelled")
        {
            await cancellation.CancelAsync();
            _ = await Assert.That(waiting).Throws<OperationCanceledException>();
        }
        else
        {
            if (outcome == "timeout")
            {
                await time.WaitForTimer(cancellationToken);
                time.Advance(TimeSpan.FromMinutes(1));
            }
            else
            {
                broker.Reply(request.Id, outcome == "granted" ? "grant" : "reject", string.Empty);
            }

            _ = await waiting;
        }

        var log = await File.ReadAllTextAsync(resources.LogPath, cancellationToken);
        _ = await Assert.That(log).Contains("event=\"request_started\"").And.Contains("event=\"request_completed\"")
            .And.Contains($"outcome=\"{outcome}\"").And.Contains(request.Id).And.DoesNotContain("private-sentinel");
    }

    [Test]
    [Arguments("delivered")]
    [Arguments("closed")]
    [Arguments("timeout")]
    [Arguments("cancelled")]
    [Arguments("failed")]
    public async Task Queue_wait_and_lifecycle_omit_names_and_items(string outcome, CancellationToken cancellationToken)
    {
        var resources = PrepareResources();
        using var diagnostics = FileDiagnosticLog.OpenSession(resources, "test", TextWriter.Null, TimeProvider.System);
        using var catalog = new AgentQueueCatalog(resources, diagnostics);
        using var queues = catalog.Register(AgentIdentity.Main("agent", "main", TestModels.PromptTemplates));
        _ = queues.Create("private-sentinel", "private-sentinel");
        if (outcome is "delivered" or "closed")
        {
            _ = await queues.Push("private-sentinel", outcome == "delivered" ? ["private-sentinel"] : [], QueueDirection.Back, true, cancellationToken);
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (outcome == "cancelled")
        {
            await cancellation.CancelAsync();
        }

        var tool = new QueueTakeTool(queues, diagnostics);
        var arguments = outcome == "failed"
            ? "{\"name\":\"missing-private-sentinel\",\"yield_after_ms\":0}"
            : "{\"name\":\"private-sentinel\",\"yield_after_ms\":0}";
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var selection = new AgentTurnSelection(new ModelSelector(model.Selector), TestModels.Resolve(model), new TestProfileFixture().Mode, SecurityProfile.Compose(false, [], [], []));
        var waiting = tool.Execute(new ToolInvocation("take", arguments), selection, cancellation.Token);
        if (outcome == "cancelled")
        {
            _ = await Assert.That(waiting).Throws<OperationCanceledException>();
        }
        else
        {
            _ = await waiting;
        }

        var log = await File.ReadAllTextAsync(resources.LogPath, cancellationToken);
        _ = await Assert.That(log).Contains("event=\"created\"").And.Contains("event=\"wait_started\"")
            .And.Contains("event=\"wait_completed\"").And.Contains($"outcome=\"{outcome}\"").And.DoesNotContain("private-sentinel");
        if (outcome is "delivered" or "closed")
        {
            _ = await Assert.That(log).Contains("event=\"closed\"");
        }
    }

    private UserSessionResources PrepareResources()
    {
        _ = Directory.CreateDirectory(_root);
        return new UserSessionResources(new StatePaths(_root, _root, _root), UserSessionId.Parse("broker"), ProjectWorkspace.FromLaunchDirectory(_root));
    }
}
