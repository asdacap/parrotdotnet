using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.State;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class StatusDrainTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Pending_status_tracks_real_turns_and_mode_changes_durably(
        CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(_root, "session.db");
        var modes = Modes();
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "first", []), LLMEvent.Completed("stop", 1, 0, 1, "unused", []), LLMEvent.Completed("stop", 1, 0, 1, "after interrupt", []));

        using (var database = SessionDatabase.Open(databasePath))
        {
            var repository = new EventRepository(database);
            await using var session = await Session(provider, database, modes, "model-1");

            var agentSessionId = AgentSessionId(repository);
            _ = await Assert.That(repository.PendingStatus(agentSessionId) is not null).IsTrue();
            await session.Interrupt(cancellationToken);
            _ = await Assert.That(repository.PendingStatus(agentSessionId) is not null).IsTrue();

            var firstStatus = ObservePayload(session, Event.PayloadOneofCase.StatusInjected, cancellationToken);
            _ = await session.Send([ConversationPart.TextPart("first prompt")], "message-1", Delivery.Steer, cancellationToken);
            await provider.Arrived(cancellationToken);
            await firstStatus;

            _ = await Assert.That(Roles(provider.Requests[0])).IsEqualTo("System | User");
            _ = await Assert.That(provider.Requests[0].Instructions).IsNotEmpty();
            var buildRequest = provider.Requests[0];
            var buildStatus = buildRequest.Messages[0].Content;
            _ = await Assert.That(buildRequest.Instructions).DoesNotContain(session.Mode.Profile.Prompt);
            await AssertContextLine(buildStatus, buildRequest, 100_000);
            _ = await Assert.That(buildStatus).StartsWith($"{session.Mode.Profile.Prompt}\n\nGenerated at: ");
            _ = await Assert.That(buildStatus).Contains("\n\nRuntime:\n- agent: main-agent");
            _ = await Assert.That(CountOccurrences(buildStatus, session.Mode.Profile.Prompt)).IsEqualTo(1);
            await AssertStatusOrder(buildStatus, "Active profile: build");
            provider.Release();
            await Settled(session, cancellationToken);

            _ = await Assert.That(repository.PendingStatus(agentSessionId) is not null).IsFalse();
            session.UpdateSelection(TestModels.Resolve(
                new ProviderModel(
                    provider,
                    new LLMModel("model-2", provider.Id) { ContextWindow = 100_000 })));
            session.Update(null, session.ResolveMode(ModeRegistry.Build));
            _ = await Assert.That(repository.PendingStatus(agentSessionId) is not null).IsFalse();

            session.Update(null, session.ResolveMode(ModeRegistry.Plan));
            _ = await Assert.That(repository.PendingStatus(agentSessionId) is not null).IsTrue();

            _ = await session.Send([ConversationPart.TextPart("plan prompt")], "message-2", Delivery.Steer, cancellationToken);
            await provider.Arrived(cancellationToken);
            _ = await Assert.That(StatusMessages(repository).Count).IsEqualTo(2);
            _ = await Assert.That(provider.Requests[1].Messages.Count(message => message.Role == LLMRole.System))
                .IsEqualTo(2);
            var planRequest = provider.Requests[1];
            var planStatus = planRequest.Messages.Single(message =>
                message.Role == LLMRole.System &&
                message.Content.Contains("Active profile: plan", StringComparison.Ordinal));
            await AssertContextLine(planStatus.Content, planRequest, 100_000);
            _ = await Assert.That(planStatus.Content).StartsWith("You are Parrot's plan mode.");
            _ = await Assert.That(planStatus.Content).Contains("to this exact file:");
            _ = await Assert.That(CountOccurrences(planStatus.Content, "You are Parrot's plan mode.")).IsEqualTo(1);
            await AssertStatusOrder(planStatus.Content, "Active profile: plan");

            await session.Interrupt(cancellationToken);
            _ = await Assert.That(repository.PendingStatus(agentSessionId) is not null).IsFalse();

            _ = await session.Send([ConversationPart.TextPart("retry")], "message-3", Delivery.Steer, cancellationToken);
            await provider.Arrived(cancellationToken);
            _ = await Assert.That(provider.Requests[2].Messages.Count(message =>
                message.Role == LLMRole.System && message.Content.Contains("Active profile:", StringComparison.Ordinal)))
                .IsEqualTo(2);
            var planArtifact = Directory.GetFiles(
                Path.Combine(_root, "sessions", "user", "root-agents", "main-agent", "plan"),
                "plan-*.md").Single();
            await File.WriteAllTextAsync(planArtifact, "# Plan", cancellationToken);
            await File.WriteAllTextAsync(
                string.Concat(planArtifact.AsSpan(0, planArtifact.Length - 3), ".tasks.json"),
                "{\"schema_version\":1,\"tasks\":[{\"name\":\"work\",\"description\":\"Do work\",\"payload\":\"Implement it\",\"acceptance_criteria\":\"Tests pass\"}]}",
                cancellationToken);
            provider.Release();
            await Settled(session, cancellationToken);

            _ = await Assert.That(StatusMessages(repository).Count).IsEqualTo(2);
        }

        using (var reopened = SessionDatabase.Open(databasePath))
        {
            var repository = new EventRepository(reopened);
            await using var session = await Session(provider, reopened, modes, "model-2");

            _ = await Assert.That(session.Mode.Profile.Id).IsEqualTo(ModeRegistry.Plan);
            _ = await Assert.That(repository.PendingStatus(AgentSessionId(repository)) is not null).IsFalse();
            _ = await Assert.That(StatusMessages(repository).Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task Blank_markdown_repairs_in_a_new_turn_before_completion(
        CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var modes = Modes();
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "candidate", []), LLMEvent.Completed("stop", 1, 0, 1, "repaired", []));
        var providerModel = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var router = TestModels.Route(providerModel);
        var sessions = new DirectAgentSessions();
        sessions.Use(router);
        var time = new ControlledTimeProvider();
        sessions.UseTimeProvider(time);
        await using var session = await Parrot.Agent.UserSession.Create(
            "repair-user",
            "main-agent",
            router.Resolve(providerModel.Selector),
            ModeRegistry.Plan,
            Resources(database, "repair-user"),
            sessions,
            new UserSessionModes(modes, TestModels.PromptTemplates, AgentTaskParser.ParseArtifact),
            TestModels.PromptTemplates,
            new TestProfileFixture().Registry,
            TestModels.SkillCatalogFactory(),
            false,
            TimeSpan.FromSeconds(30),
            TimeProvider.System,
            static () => new EventBroker());

        _ = await session.Send([ConversationPart.TextPart("plan")], "message", Delivery.Queue, cancellationToken);
        await provider.Arrived(cancellationToken);
        var planArtifact = Directory.GetFiles(
            Path.Combine(_root, "sessions", "repair-user", "root-agents", "main-agent", "plan"),
            "plan-*.md").Single();
        var taskArtifact = string.Concat(planArtifact.AsSpan(0, planArtifact.Length - 3), ".tasks.json");
        await File.WriteAllTextAsync(
            taskArtifact,
            "{\"schema_version\":1,\"tasks\":[{\"name\":\"work\",\"description\":\"Do work\",\"payload\":\"Implement it\",\"acceptance_criteria\":\"Tests pass\"}]}",
            cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);

        var repairing = repository.Replay();
        var repairDiagnostic = repairing.Single(published =>
            published.PayloadCase == Event.PayloadOneofCase.PlanValidationRepairInjected).PlanValidationRepairInjected.Diagnostic;
        _ = await Assert.That(repairing.Any(published =>
            published.PayloadCase == Event.PayloadOneofCase.PlanCompleted)).IsFalse();
        _ = await Assert.That(repairing.Count(published =>
            published.PayloadCase == Event.PayloadOneofCase.TurnEnded)).IsEqualTo(1);
        _ = await Assert.That(provider.Requests[1].Messages[^1].Role).IsEqualTo(LLMRole.User);
        _ = await Assert.That(provider.Requests[1].Messages[^1].Content).IsEqualTo(repairDiagnostic);
        var repairingActivity = sessions.Sessions.Single().Activity.Capture();
        _ = await Assert.That(repairingActivity.Recent).Count().IsEqualTo(1);
        _ = await Assert.That(repairingActivity.Recent[0].Content).IsEqualTo("candidate");

        time.Advance(TimeSpan.FromSeconds(2));
        await File.WriteAllTextAsync(planArtifact, "# Repaired", cancellationToken);
        var turnCompleted = ObservePayload(session, Event.PayloadOneofCase.ModeTurnCompleted, cancellationToken);
        provider.Release();
        await turnCompleted;

        var completed = repository.Replay().ToArray();
        var repair = Array.FindIndex(completed, published =>
            published.PayloadCase == Event.PayloadOneofCase.PlanValidationRepairInjected);
        var plan = Array.FindIndex(completed, published =>
            published.PayloadCase == Event.PayloadOneofCase.PlanCompleted);
        var firstEnded = Array.FindIndex(completed, published =>
            published.PayloadCase == Event.PayloadOneofCase.TurnEnded);
        var lastEnded = Array.FindLastIndex(completed, published =>
            published.PayloadCase == Event.PayloadOneofCase.TurnEnded);
        _ = await Assert.That(firstEnded).IsLessThan(repair);
        _ = await Assert.That(repair).IsLessThan(lastEnded);
        _ = await Assert.That(lastEnded).IsLessThan(plan);
        _ = await Assert.That(plan).IsLessThan(Array.FindIndex(completed, published =>
            published.PayloadCase == Event.PayloadOneofCase.ModeTurnCompleted));
        _ = await Assert.That(completed.Count(published =>
            published.PayloadCase == Event.PayloadOneofCase.ModeTurnCompleted)).IsEqualTo(1);
        var activity = sessions.Sessions.Single().Activity.Capture();
        _ = await Assert.That(activity.Recent).Count().IsEqualTo(2);
        _ = await Assert.That(activity.Recent[0].Content).IsEqualTo("candidate");
        _ = await Assert.That(activity.Recent[0].Age).IsEqualTo(TimeSpan.FromSeconds(2));
        _ = await Assert.That(activity.Recent[1].Content).IsEqualTo("repaired");
        _ = await Assert.That(activity.Recent[1].Age).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task Plan_completion_uses_the_user_session_profile_for_the_main_agent(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var modes = Modes();
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "steered", []), LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var providerModel = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var router = TestModels.Route(providerModel);
        var sessions = new DirectAgentSessions();
        sessions.Use(router);
        await using var session = await Parrot.Agent.UserSession.Create(
            "user",
            "main-agent",
            router.Resolve(providerModel.Selector),
            ModeRegistry.Plan,
            Resources(database, "user"),
            sessions,
            new UserSessionModes(modes, TestModels.PromptTemplates, AgentTaskParser.ParseArtifact),
            TestModels.PromptTemplates,
            new TestProfileFixture().Registry,
            TestModels.SkillCatalogFactory(),
            false,
            TimeSpan.FromSeconds(30),
            TimeProvider.System,
            static () => new EventBroker());

        _ = await session.Send([ConversationPart.TextPart("steer")], "steer", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        var planArtifact = Directory.GetFiles(
            Path.Combine(_root, "sessions", "user", "root-agents", "main-agent", "plan"),
            "plan-*.md").Single();
        await File.WriteAllTextAsync(planArtifact, "  # Plan\n", cancellationToken);
        var taskArtifact = string.Concat(planArtifact.AsSpan(0, planArtifact.Length - 3), ".tasks.json");
        await File.WriteAllTextAsync(taskArtifact, "{\"schema_version\":1,\"tasks\":[{\"name\":\"work\",\"description\":\"Do work\",\"payload\":\"Implement it\",\"acceptance_criteria\":\"Tests pass\"}]}", cancellationToken);
        provider.Release();
        await Settled(session, cancellationToken);
        _ = await Assert.That(repository.Replay().Any(published =>
            published.PayloadCase == Event.PayloadOneofCase.PlanCompleted)).IsFalse();

        _ = await session.Send([ConversationPart.TextPart("plan")], "message", Delivery.Queue, cancellationToken);
        await provider.Arrived(cancellationToken);
        var turnCompleted = ObservePayload(session, Event.PayloadOneofCase.ModeTurnCompleted, cancellationToken);
        provider.Release();
        await turnCompleted;

        var mainAgentSessionId = AgentSessionId(repository);
        var events = repository.Replay();
        var plan = events.Single(published => published.PayloadCase == Event.PayloadOneofCase.PlanCompleted);
        var ended = events.Last(published => published.PayloadCase == Event.PayloadOneofCase.TurnEnded);

        _ = await Assert.That(plan.AgentSessionId).IsEqualTo(mainAgentSessionId);
        _ = await Assert.That(plan.PlanCompleted.AgentSessionId).IsEqualTo(mainAgentSessionId);
        _ = await Assert.That(plan.PlanCompleted.Markdown).IsEqualTo("# Plan");
        _ = await Assert.That(plan.PlanCompleted.TaskTree).IsNotNull();
        _ = await Assert.That(plan.PlanCompleted.TaskTree.RootNodes[0].Name).IsEqualTo("work");
        _ = await Assert.That(plan.PlanCompleted.TaskTree.RootNodes[0].Status)
            .IsEqualTo(AgentTaskProgressStatus.Pending);
        _ = await Assert.That(plan.PlanCompleted.TaskDeclarations).Count().IsEqualTo(1);
        _ = await Assert.That(plan.PlanCompleted.TaskDeclarations[0].Name).IsEqualTo("work");
        _ = await Assert.That(plan.PlanCompleted.TaskDeclarations[0].Description).IsEqualTo("Do work");
        _ = await Assert.That(plan.PlanCompleted.TaskDeclarations[0].AcceptanceCriteria).IsEqualTo("Tests pass");
        _ = await Assert.That(plan.PlanCompleted.TaskDeclarations[0].Instruction).IsEqualTo("Implement it");
        _ = await Assert.That(plan.PlanCompleted.TaskDeclarations[0].Status)
            .IsEqualTo(AgentTaskProgressStatus.Pending);
        var sequence = events.ToArray();
        _ = await Assert.That(Array.IndexOf(sequence, ended)).IsLessThan(Array.IndexOf(sequence, plan));
        _ = await Assert.That(Array.IndexOf(sequence, plan)).IsLessThan(Array.FindIndex(sequence, published =>
            published.PayloadCase == Event.PayloadOneofCase.ModeTurnCompleted));
    }

    [Test]
    public async Task Status_tool_reports_runtime_without_the_active_profile_prompt(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        var modes = Modes();
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, string.Empty, [new LLMToolCall("status-call", "status", "{}")]),
            LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        await using var session = await Session(provider, database, modes, "model", includeStatusTool: true);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "message", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);

        var result = provider.Requests[1].Messages.Single(message => message.Role == LLMRole.Tool).Content;
        _ = await Assert.That(result).StartsWith("Runtime:\n- agent: main-agent");
        _ = await Assert.That(result).DoesNotContain(session.Mode.Profile.Prompt);
        _ = await Assert.That(result).DoesNotContain("Active profile:");
        _ = await Assert.That(result).DoesNotContain("Model:");
        _ = await Assert.That(result).Contains("Self: 1 (0.00% cache) / 1; 1 tool; cost $0");
        _ = await Assert.That(result).Contains("Cumulative (self + descendants): 1 (0.00% cache) / 1; 1 tool");
        _ = await Assert.That(result).Contains($"{provider.Id}/model/unspecified/default:");
        _ = await Assert.That(provider.Requests[0].Messages[0].Content).DoesNotContain("Statistics (lifetime):");

        provider.Release();
        await Settled(session, cancellationToken);
    }

    [Test]
    public async Task Tool_rounds_reuse_one_durable_status_message(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var modes = Modes();
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, string.Empty, [new LLMToolCall("call", "missing", "{}")]),
            LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        await using var session = await Session(provider, database, modes, "model");

        _ = await session.Send([ConversationPart.TextPart("prompt")], "message", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        session.UpdateSelection(TestModels.Resolve(
            new ProviderModel(provider, new LLMModel("next-model", provider.Id))));
        session.Update(null, session.ResolveMode(ModeRegistry.Plan));
        provider.Release();
        await provider.Arrived(cancellationToken);

        _ = await Assert.That(provider.Requests.Count).IsEqualTo(2);
        _ = await Assert.That(provider.Requests.All(request => request.Instructions.Length > 0)).IsTrue();
        _ = await Assert.That(string.Join(",", provider.Requests.Select(request => request.Model)))
            .IsEqualTo("model,model");
        _ = await Assert.That(string.Join(",", provider.Requests.Select(request => request.Messages.Count(message =>
            message.Role == LLMRole.System && message.Content.Contains("Active profile:", StringComparison.Ordinal)))))
            .IsEqualTo("1,1");

        provider.Release();
        await Settled(session, cancellationToken);
        _ = await Assert.That(StatusMessages(repository).Count).IsEqualTo(1);
        _ = await Assert.That(repository.PendingStatus(AgentSessionId(repository)) is not null).IsTrue();
    }

    private static async Task AssertContextLine(string content, LLMRequest request, int contextLimit)
    {
        var estimated = Compactor.EstimateInputTokens(new ProviderModel(new ScriptedProvider(string.Empty), new LLMModel(request.Model, "scripted")), request.Instructions, request.Tools, request.Messages);
        var usage = estimated >= contextLimit
            ? 100
            : (int)(estimated * 100L / contextLimit);
        var expected = $"Context: {usage}% used ({TokenCountFormatter.Format(estimated)} estimated tokens / {TokenCountFormatter.Format(contextLimit)} limit); reminders every 10%; automatic compaction at 90%.";
        _ = await Assert.That(content).Contains(expected);
    }

    private static async Task AssertStatusOrder(string content, string selection)
    {
        var runtime = content.IndexOf("Runtime:", StringComparison.Ordinal);
        var activeSelection = content.IndexOf(selection, StringComparison.Ordinal);

        _ = await Assert.That(runtime).IsGreaterThan(0);
        _ = await Assert.That(runtime).IsLessThan(activeSelection);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;

        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string Roles(LLMRequest request) =>
        string.Join(" | ", request.Messages.Select(message => message.Role));

    private static IReadOnlyList<LLMMessage> StatusMessages(IEventRepository repository) =>
        [.. repository.ModelHistory(AgentSessionId(repository)).Where(message =>
            message.Role == LLMRole.System && message.Content.Contains("Active profile:", StringComparison.Ordinal))];

    private static string AgentSessionId(IEventRepository repository) =>
        repository.SessionState("user", ModeRegistry.Build).AgentSessionId;

    private static async Task ObservePayload(
        Parrot.Agent.IUserSession session,
        Event.PayloadOneofCase payload,
        CancellationToken cancellationToken)
    {
        await foreach (var published in session.Listen(null, cancellationToken).ConfigureAwait(false))
        {
            if (published.PayloadCase == payload)
            {
                return;
            }
        }
    }

    private static Task Settled(Parrot.Agent.IUserSession session, CancellationToken cancellationToken) =>
        TestPolling.Until(
            () => session.History() is { Count: > 0 } history && history[^1].StartsWith("assistant:", StringComparison.Ordinal),
            cancellationToken);

    private Task<Parrot.Agent.IUserSession> Session(
        SteppedProvider provider,
        SessionDatabase database,
        ModeRegistry modes,
        string model) => Session(provider, database, modes, model, false);

    private Task<Parrot.Agent.IUserSession> Session(
        SteppedProvider provider,
        SessionDatabase database,
        ModeRegistry modes,
        string model,
        bool includeStatusTool)
    {
        var providerModel = new ProviderModel(provider, new LLMModel(model, provider.Id) { ContextWindow = 100_000 });
        var router = TestModels.Route(providerModel);
        var sessions = new DirectAgentSessions();
        sessions.Use(router);
        if (includeStatusTool)
        {
            sessions.IncludeStatusTool();
        }

        return Parrot.Agent.UserSession.Create(
            "user",
            "main-agent",
            router.Resolve(providerModel.Selector),
            ModeRegistry.Build,
            Resources(database, "user"),
            sessions,
            new UserSessionModes(modes, TestModels.PromptTemplates, AgentTaskParser.ParseArtifact),
            TestModels.PromptTemplates,
            new TestProfileFixture().Registry,
            TestModels.SkillCatalogFactory(),
            false,
            TimeSpan.FromSeconds(30),
            TimeProvider.System,
            static () => new EventBroker());
    }

    private ModeRegistry Modes()
    {
        var configuration = Configuration.Load(
            Path.Combine(_root, "config.yaml"),
            Path.Combine(_root, "predefined_config.yaml"));
        return new ModeRegistry(
            new ProfileRegistry(
                configuration.Profiles,
                configuration.SandboxRules,
                [],
                configuration.DisabledTools),
            configuration.DefaultProfile);
    }

    private SessionResourceLease Resources(SessionDatabase database, string ownerId)
    {
        var paths = new StatePaths(_root, Path.Combine(_root, "config"), Path.Combine(_root, "data"));
        var workspace = ProjectWorkspace.FromLaunchDirectory(_root);
        var resources = new UserSessionResources(paths, UserSessionId.Parse(ownerId), workspace);
        var diagnostics = (IDiagnosticLog?)FileDiagnosticLog.OpenSession(resources, "test", TextWriter.Null, TimeProvider.System);
        try
        {
            var lease = SessionResourceLease.Own(resources, database, diagnostics ?? throw new InvalidOperationException("Missing diagnostics"));
            diagnostics = null;
            return lease;
        }
        finally
        {
            diagnostics?.Dispose();
        }
    }
}
