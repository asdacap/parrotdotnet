using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.State;
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
        using var provider = new SteppedProvider(Answer("first"), Answer("unused"), Answer("after interrupt"));

        using (var database = SessionDatabase.Open(databasePath))
        {
            var repository = new EventRepository(database);
            await using var session = Session(provider, database, modes, "model-1");

            var agentSessionId = AgentSessionId(repository);
            _ = await Assert.That(repository.StatusPromptPending(agentSessionId)).IsTrue();
            await session.Interrupt(cancellationToken);
            _ = await Assert.That(repository.StatusPromptPending(agentSessionId)).IsTrue();

            var firstStatus = ObserveStatus(session, cancellationToken);
            _ = await session.Send("first prompt", "message-1", Delivery.Steer, cancellationToken);
            await provider.Arrived(cancellationToken);
            await firstStatus;

            _ = await Assert.That(Roles(provider.Requests[0])).IsEqualTo("System | User");
            _ = await Assert.That(provider.Requests[0].Instructions).IsNotEmpty();
            var buildStatus = provider.Requests[0].Messages[0].Content;
            _ = await Assert.That(buildStatus).StartsWith($"{session.Mode.Prompt}\n\nRuntime:\n- agent: main-agent (");
            _ = await Assert.That(CountOccurrences(buildStatus, session.Mode.Prompt)).IsEqualTo(1);
            await AssertStatusOrder(buildStatus, "Active profile: build");
            provider.Release();
            await Settled(session);

            _ = await Assert.That(repository.StatusPromptPending(agentSessionId)).IsFalse();
            session.UpdateSelection(TestModels.Resolve(
                new ProviderModel(provider, new LLMModel("model-2", provider.Id))));
            session.UpdateMode(ModeRegistry.Build);
            _ = await Assert.That(repository.StatusPromptPending(agentSessionId)).IsFalse();

            session.UpdateMode(ModeRegistry.Plan);
            _ = await Assert.That(repository.StatusPromptPending(agentSessionId)).IsTrue();

            _ = await session.Send("plan prompt", "message-2", Delivery.Steer, cancellationToken);
            await provider.Arrived(cancellationToken);
            _ = await Assert.That(StatusMessages(repository).Count).IsEqualTo(2);
            _ = await Assert.That(provider.Requests[1].Messages.Count(message => message.Role == LLMRole.System))
                .IsEqualTo(2);
            var planStatus = provider.Requests[1].Messages.Single(message =>
                message.Role == LLMRole.System &&
                message.Content.Contains("Active profile: plan", StringComparison.Ordinal));
            _ = await Assert.That(planStatus.Content).StartsWith("You are Parrot's plan mode.");
            _ = await Assert.That(planStatus.Content).Contains("to this exact file:");
            _ = await Assert.That(CountOccurrences(planStatus.Content, "You are Parrot's plan mode.")).IsEqualTo(1);
            await AssertStatusOrder(planStatus.Content, "Active profile: plan");

            await session.Interrupt(cancellationToken);
            _ = await Assert.That(repository.StatusPromptPending(agentSessionId)).IsFalse();

            _ = await session.Send("retry", "message-3", Delivery.Steer, cancellationToken);
            await provider.Arrived(cancellationToken);
            _ = await Assert.That(provider.Requests[2].Messages.Count(message =>
                message.Role == LLMRole.System && message.Content.Contains("Active profile:", StringComparison.Ordinal)))
                .IsEqualTo(2);
            provider.Release();
            await Settled(session);

            _ = await Assert.That(StatusMessages(repository).Count).IsEqualTo(2);
        }

        using (var reopened = SessionDatabase.Open(databasePath))
        {
            var repository = new EventRepository(reopened);
            await using var session = Session(provider, reopened, modes, "model-2");

            _ = await Assert.That(session.Mode.Id).IsEqualTo(ModeRegistry.Plan);
            _ = await Assert.That(repository.StatusPromptPending(AgentSessionId(repository))).IsFalse();
            _ = await Assert.That(StatusMessages(repository).Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task Blank_markdown_repairs_in_the_same_turn_before_completion(
        CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var modes = Modes();
        using var provider = new SteppedProvider(Answer("candidate"), Answer("repaired"));
        var providerModel = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var router = TestModels.Route(providerModel);
        var sessions = new DirectAgentSessions();
        sessions.Use(router);
        await using var session = new Parrot.Agent.UserSession(
            "repair-user",
            "main-agent",
            router.Resolve(providerModel.Selector),
            ModeRegistry.Plan,
            Resources(database, "repair-user"),
            sessions,
            OwnerModes(modes, "repair-user"),
            TestModels.ProfileRegistry(),
            false,
            TimeSpan.FromSeconds(30),
            TimeProvider.System);

        _ = await session.Send("plan", "message", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        var planArtifact = Directory.GetFiles(
            Path.Combine(
                _root,
                "sessions",
                "repair-user",
                "scratch",
                repository.SessionState("repair-user", ModeRegistry.Build).AgentSessionId,
                "plan"),
            "plan-*.md").Single();
        var taskArtifact = string.Concat(planArtifact.AsSpan(0, planArtifact.Length - 3), ".tasks.json");
        await File.WriteAllTextAsync(
            taskArtifact,
            "{\"schema_version\":1,\"tasks\":[{\"name\":\"work\",\"description\":\"Do work\",\"payload\":\"Implement it\",\"acceptance_criteria\":\"Tests pass\"}]}",
            cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);

        var repairing = repository.Replay();
        _ = await Assert.That(repairing.Count(published =>
            published.PayloadCase == Event.PayloadOneofCase.PlanValidationRepairInjected)).IsEqualTo(1);
        _ = await Assert.That(repairing.Any(published =>
            published.PayloadCase == Event.PayloadOneofCase.PlanCompleted)).IsFalse();
        _ = await Assert.That(repairing.Any(published =>
            published.PayloadCase == Event.PayloadOneofCase.TurnEnded)).IsFalse();

        await File.WriteAllTextAsync(planArtifact, "# Repaired", cancellationToken);
        provider.Release();
        await Settled(session);

        var completed = repository.Replay().ToArray();
        var repair = Array.FindIndex(completed, published =>
            published.PayloadCase == Event.PayloadOneofCase.PlanValidationRepairInjected);
        var plan = Array.FindIndex(completed, published =>
            published.PayloadCase == Event.PayloadOneofCase.PlanCompleted);
        var ended = Array.FindIndex(completed, published =>
            published.PayloadCase == Event.PayloadOneofCase.TurnEnded);
        _ = await Assert.That(repair).IsLessThan(plan);
        _ = await Assert.That(plan).IsLessThan(ended);
    }

    [Test]
    public async Task Plan_completion_uses_the_user_session_profile_for_the_main_agent(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var modes = Modes();
        using var provider = new SteppedProvider(Answer("done"));
        var providerModel = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var router = TestModels.Route(providerModel);
        var sessions = new DirectAgentSessions();
        sessions.Use(router);
        await using var session = new Parrot.Agent.UserSession(
            "user",
            "main-agent",
            router.Resolve(providerModel.Selector),
            ModeRegistry.Plan,
            Resources(database, "user"),
            sessions,
            OwnerModes(modes, "user"),
            TestModels.ProfileRegistry(),
            false,
            TimeSpan.FromSeconds(30),
            TimeProvider.System);

        _ = await session.Send("plan", "message", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        var planArtifact = Directory.GetFiles(
            Path.Combine(_root, "sessions", "user", "scratch", AgentSessionId(repository), "plan"),
            "plan-*.md").Single();
        await File.WriteAllTextAsync(planArtifact, "  # Plan\n", cancellationToken);
        var taskArtifact = string.Concat(planArtifact.AsSpan(0, planArtifact.Length - 3), ".tasks.json");
        await File.WriteAllTextAsync(taskArtifact, "{\"schema_version\":1,\"tasks\":[{\"name\":\"work\",\"description\":\"Do work\",\"payload\":\"Implement it\",\"acceptance_criteria\":\"Tests pass\"}]}", cancellationToken);
        provider.Release();
        await Settled(session);

        var mainAgentSessionId = AgentSessionId(repository);
        var events = repository.Replay();
        var plan = events.Single(published => published.PayloadCase == Event.PayloadOneofCase.PlanCompleted);
        var ended = events.Single(published => published.PayloadCase == Event.PayloadOneofCase.TurnEnded);

        _ = await Assert.That(plan.AgentSessionId).IsEqualTo(mainAgentSessionId);
        _ = await Assert.That(plan.PlanCompleted.AgentSessionId).IsEqualTo(mainAgentSessionId);
        _ = await Assert.That(plan.PlanCompleted.Markdown).IsEqualTo("# Plan");
        var sequence = events.ToArray();
        _ = await Assert.That(Array.IndexOf(sequence, plan)).IsLessThan(Array.IndexOf(sequence, ended));
    }

    [Test]
    public async Task Status_tool_reports_runtime_without_the_active_profile_prompt(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        var modes = Modes();
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("status-call", "status", "{}")),
            Answer("done"));
        await using var session = Session(provider, database, modes, "model", includeStatusTool: true);

        _ = await session.Send("prompt", "message", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);

        var result = provider.Requests[1].Messages.Single(message => message.Role == LLMRole.Tool).Content;
        _ = await Assert.That(result).StartsWith("Runtime:\n- agent: main-agent (");
        _ = await Assert.That(result).DoesNotContain(session.Mode.Prompt);
        _ = await Assert.That(result).DoesNotContain("Active profile:");
        _ = await Assert.That(result).DoesNotContain("Model:");

        provider.Release();
        await Settled(session);
    }

    [Test]
    public async Task Tool_rounds_reuse_one_durable_status_message(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var modes = Modes();
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call", "missing", "{}")),
            Answer("done"));
        await using var session = Session(provider, database, modes, "model");

        _ = await session.Send("prompt", "message", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        session.UpdateSelection(TestModels.Resolve(
            new ProviderModel(provider, new LLMModel("next-model", provider.Id))));
        session.UpdateMode(ModeRegistry.Plan);
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
        await Settled(session);
        _ = await Assert.That(StatusMessages(repository).Count).IsEqualTo(1);
        _ = await Assert.That(repository.StatusPromptPending(AgentSessionId(repository))).IsTrue();
    }

    private static LLMEvent Answer(string text, params LLMToolCall[] toolCalls) =>
        LLMEvent.Completed("stop", 1, 0, 1, text, toolCalls);

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

    private static IReadOnlyList<LLMMessage> StatusMessages(EventRepository repository) =>
        [.. repository.ModelHistory(AgentSessionId(repository)).Where(message =>
            message.Role == LLMRole.System && message.Content.Contains("Active profile:", StringComparison.Ordinal))];

    private static string AgentSessionId(EventRepository repository) =>
        repository.SessionState("user", ModeRegistry.Build).AgentSessionId;

    private static async Task ObserveStatus(
        Parrot.Agent.UserSession session,
        CancellationToken cancellationToken)
    {
        await foreach (var published in session.Listen(cancellationToken).ConfigureAwait(false))
        {
            if (published.PayloadCase == Event.PayloadOneofCase.StatusInjected)
            {
                return;
            }
        }
    }

    private static async Task Settled(Parrot.Agent.UserSession session)
    {
        while (session.History().Count == 0 || !session.History()[^1].StartsWith("assistant:", StringComparison.Ordinal))
        {
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private Parrot.Agent.UserSession Session(
        SteppedProvider provider,
        SessionDatabase database,
        ModeRegistry modes,
        string model) => Session(provider, database, modes, model, false);

    private Parrot.Agent.UserSession Session(
        SteppedProvider provider,
        SessionDatabase database,
        ModeRegistry modes,
        string model,
        bool includeStatusTool)
    {
        var providerModel = new ProviderModel(provider, new LLMModel(model, provider.Id));
        var router = TestModels.Route(providerModel);
        var sessions = new DirectAgentSessions();
        sessions.Use(router);
        if (includeStatusTool)
        {
            sessions.IncludeStatusTool();
        }

        return new Parrot.Agent.UserSession(
            "user",
            "main-agent",
            router.Resolve(providerModel.Selector),
            ModeRegistry.Build,
            Resources(database, "user"),
            sessions,
            OwnerModes(modes, "user"),
            TestModels.ProfileRegistry(),
            false,
            TimeSpan.FromSeconds(30),
            TimeProvider.System);
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
        return SessionResourceLease.Own(
            new UserSessionResources(paths, UserSessionId.Parse(ownerId), workspace),
            database);
    }

    private UserSessionModes OwnerModes(ModeRegistry modes, string ownerId) =>
        new(modes, Path.Combine(_root, "sessions", ownerId, "plan"));
}
