using Parrot.Agent;
using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.State;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class AgentOperationDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-operation-diagnostics-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    [Arguments("completed")]
    [Arguments("failed")]
    [Arguments("cancelled")]
    [Arguments("unknown_tool")]
    [Arguments("turn_failed")]
    [Arguments("turn_cancelled")]
    [Arguments("multiple_tools")]
    [Arguments("runaway")]
    public async Task Tool_and_turn_outcomes_are_correlated_without_payloads(string outcome, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(_root);
        var resources = new UserSessionResources(
            new StatePaths(_root, _root, _root),
            UserSessionId.Parse("user-session-diagnostics"),
            ProjectWorkspace.FromLaunchDirectory(_root));
        _ = Directory.CreateDirectory(resources.Root);
        using var diagnostics = FileDiagnosticLog.OpenSession(resources, "test", TextWriter.Null, TimeProvider.System);
        using var database = SessionDatabase.Open(":memory:");
        using var events = new EventBroker();
        var repository = new EventRepository(database);
        ITool tool = new OutcomeTool(outcome);
        var toolResponse = LLMEvent.Completed(
            "tool_calls",
            1,
            0,
            1,
            "private-response-sentinel",
            [new LLMToolCall("call-diagnostic", outcome == "unknown_tool" ? "private-unknown-sentinel" : tool.Name, "{\"value\":\"private-argument-sentinel\"}")]);
        var toolCycles = outcome switch { "multiple_tools" => 3, "runaway" => new TestProfileFixture().Mode.Profile.MaxTurns, _ => 1 };
        using var provider = new SteppedProvider([toolResponse,
            .. Enumerable.Range(1, toolCycles - 1).Select(index => LLMEvent.Completed(
                "tool_calls",
                1,
                0,
                1,
                "private-response-sentinel",
                [new LLMToolCall("call-diagnostic-" + index, tool.Name, "{\"value\":\"private-argument-sentinel\"}")])),
            LLMEvent.Completed("stop", 1, 0, 1, "private-final-sentinel", [])]);
        ILLMProvider selectedProvider = outcome == "turn_failed" ? new TerminalFailureProvider("private-exception-sentinel") : provider;
        var model = new ProviderModel(selectedProvider, new LLMModel("model", selectedProvider.Id));
        var identity = AgentIdentity.Main("agent-diagnostics", string.Empty, TestModels.PromptTemplates);
        using var dependencies = TestModels.Dependencies(identity, events, repository, cancellationToken);
        IToolFactory toolFactory = new FixedToolFactory(tool);
        await using IAgentSession session = new AgentSession(
            identity,
            AgentSessionParentScope.Root(),
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            events,
            repository,
            [toolFactory],
            new TestToolDefinitionsFixture(tool.Name).Definitions,
            TestModels.MaterializePrompt(identity, _root, _root),
            new ToolOutputBlobStore(_root),
            TestModels.CompactionGroupBlobs(),
            new Compactor(int.MaxValue, 30, 60_000, 1024, TestModels.PromptTemplates),
            new ProviderSessions(diagnostics, identity.SessionId, null),
            new ContextCadence(),
            TestModels.PromptTemplates,
            dependencies.ChildQuestions,
            dependencies.ExitReminder,
            dependencies.Profile,
            new TestCompletionCallbacksFixture(dependencies.ChildQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, repository, events).Callbacks,
            new SecurityProfileTestFixture(SecurityProfile.Compose(readOnly: false, [], [], [])).Security,
            dependencies.Status,
            new AgentSessionActivity(TimeProvider.System),
            diagnostics,
            cancellationToken);
        _ = await session.Send([ConversationPart.TextPart("private-prompt-sentinel")], "message", Delivery.Steer, cancellationToken);
        if (outcome == "turn_cancelled")
        {
            await provider.Arrived(cancellationToken);
            await session.Interrupt(cancellationToken);
        }
        else if (outcome != "turn_failed")
        {
            var expectedCalls = outcome == "runaway" ? toolCycles : toolCycles + 1;
            for (var call = 0; call < expectedCalls; call++)
            {
                await provider.Arrived(cancellationToken);
                provider.Release();
            }
        }

        await session.Settled();
        var log = await File.ReadAllTextAsync(resources.LogPath, cancellationToken);
        if (outcome.StartsWith("turn_", StringComparison.Ordinal))
        {
            _ = await Assert.That(log).Contains("outcome=\"" + outcome[5..] + "\"");
        }
        else
        {
            _ = await Assert.That(log).Contains("category=\"tool\" event=\"started\"");
            _ = await Assert.That(log).Contains("category=\"tool\" event=\"finished\"");
            _ = await Assert.That(log).Contains("outcome=\"" + (outcome is "multiple_tools" or "runaway" ? "completed" : outcome) + "\"");
            _ = await Assert.That(log).Contains("correlation=\"call-diagnostic\"");
        }

        var turnLines = log.Split('\n').Where(line => line.Contains("category=\"turn\"", StringComparison.Ordinal)).ToArray();
        _ = await Assert.That(turnLines.Count(line => line.Contains("event=\"started\"", StringComparison.Ordinal))).IsEqualTo(1);
        _ = await Assert.That(turnLines.Count(line => line.Contains("event=\"finished\"", StringComparison.Ordinal))).IsEqualTo(1);
        if (outcome == "runaway")
        {
            _ = await Assert.That(turnLines.Single(line => line.Contains("event=\"finished\"", StringComparison.Ordinal)))
                .Contains("outcome=\"failed\"").And.Contains("error=\"runaway\"");
        }

        _ = await Assert.That(log).Contains("category=\"turn\" event=\"finished\"");
        _ = await Assert.That(log).Contains("duration_ms=");
        _ = await Assert.That(log).DoesNotContain("private-");
    }

    private sealed class OutcomeTool(string outcome) : ITool
    {
        public string Name => "settled";

        public Task<ToolExecutionResult> Execute(ToolInvocation invocation, AgentTurnSelection selection, CancellationToken cancellationToken) => outcome switch
        {
            "failed" => throw new InvalidOperationException("private-exception-sentinel"),
            "cancelled" => throw new OperationCanceledException("private-cancellation-sentinel"),
            _ => Task.FromResult<ToolExecutionResult>("private-result-sentinel"),
        };
    }
}
