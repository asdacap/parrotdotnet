using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Protocol;
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
            await using var session = Session(provider, repository, modes, "model-1");

            var agentSessionId = AgentSessionId(repository);
            _ = await Assert.That(repository.StatusPromptPending(agentSessionId)).IsTrue();
            await session.Interrupt(cancellationToken);
            _ = await Assert.That(repository.StatusPromptPending(agentSessionId)).IsTrue();

            var firstStatus = ObserveCommittedStatus(session, repository, cancellationToken);
            _ = await session.Send("first prompt", "message-1", Delivery.Steer, cancellationToken);
            await provider.Arrived(cancellationToken);
            await firstStatus;

            _ = await Assert.That(Roles(provider.Requests[0])).IsEqualTo("System | System | User");
            _ = await Assert.That(provider.Requests[0].Messages[1].Content).Contains("Active profile: build");
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
                .IsEqualTo(3);
            _ = await Assert.That(provider.Requests[1].Messages.Any(message =>
                message.Role == LLMRole.System &&
                message.Content.Contains("Active profile: plan", StringComparison.Ordinal))).IsTrue();

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
            await using var session = Session(provider, repository, modes, "model-2");

            _ = await Assert.That(session.Mode.Id).IsEqualTo(ModeRegistry.Plan);
            _ = await Assert.That(repository.StatusPromptPending(AgentSessionId(repository))).IsFalse();
            _ = await Assert.That(StatusMessages(repository).Count).IsEqualTo(2);
        }
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
            repository,
            sessions,
            modes);

        _ = await session.Send("plan", "message", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        await File.WriteAllTextAsync(session.Mode.PlanArtifact, "  # Plan\n", cancellationToken);
        provider.Release();
        await Settled(session);

        var mainAgentSessionId = AgentSessionId(repository);
        var events = repository.Replay();
        var plan = events.Single(published => published.PayloadCase == Event.PayloadOneofCase.PlanCompleted);
        var ended = events.Single(published => published.PayloadCase == Event.PayloadOneofCase.TurnEnded);

        _ = await Assert.That(plan.AgentSessionId).IsEqualTo(mainAgentSessionId);
        _ = await Assert.That(plan.PlanCompleted.SessionId).IsEqualTo(mainAgentSessionId);
        _ = await Assert.That(plan.PlanCompleted.Markdown).IsEqualTo("# Plan");
        var sequence = events.ToArray();
        _ = await Assert.That(Array.IndexOf(sequence, plan)).IsLessThan(Array.IndexOf(sequence, ended));
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
        await using var session = Session(provider, repository, modes, "model");

        _ = await session.Send("prompt", "message", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        session.UpdateSelection(TestModels.Resolve(
            new ProviderModel(provider, new LLMModel("next-model", provider.Id))));
        session.UpdateMode(ModeRegistry.Plan);
        provider.Release();
        await provider.Arrived(cancellationToken);

        _ = await Assert.That(provider.Requests.Count).IsEqualTo(2);
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

    private static string Roles(LLMRequest request) =>
        string.Join(" | ", request.Messages.Select(message => message.Role));

    private static IReadOnlyList<LLMMessage> StatusMessages(EventRepository repository) =>
        [.. repository.ModelHistory(AgentSessionId(repository)).Where(message =>
            message.Role == LLMRole.System && message.Content.Contains("Active profile:", StringComparison.Ordinal))];

    private static string AgentSessionId(EventRepository repository) =>
        repository.SessionState("user", ModeRegistry.Build).AgentSessionId;

    private static async Task ObserveCommittedStatus(
        Parrot.Agent.UserSession session,
        EventRepository repository,
        CancellationToken cancellationToken)
    {
        await foreach (var published in session.Listen(cancellationToken).ConfigureAwait(false))
        {
            if (published.PayloadCase != Event.PayloadOneofCase.StatusInjected)
            {
                continue;
            }

            _ = await Assert.That(repository.Replay().Any(item =>
                item.Id == published.Id && item.PayloadCase == Event.PayloadOneofCase.StatusInjected)).IsFalse();
            return;
        }
    }

    private static async Task Settled(Parrot.Agent.UserSession session)
    {
        while (session.History().Count == 0 || !session.History()[^1].StartsWith("assistant:", StringComparison.Ordinal))
        {
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private static Parrot.Agent.UserSession Session(
        SteppedProvider provider,
        EventRepository repository,
        ModeRegistry modes,
        string model)
    {
        var providerModel = new ProviderModel(provider, new LLMModel(model, provider.Id));
        var router = TestModels.Route(providerModel);
        var sessions = new DirectAgentSessions();
        sessions.Use(router);
        return new Parrot.Agent.UserSession(
            "user",
            "main-agent",
            router.Resolve(providerModel.Selector),
            ModeRegistry.Build,
            repository,
            sessions,
            modes);
    }

    private ModeRegistry Modes()
    {
        var configuration = Configuration.Load(
            Path.Combine(_root, "config.yaml"),
            Path.Combine(_root, "predefined_config.yaml"));
        return new ModeRegistry(
            Path.Combine(_root, "plans"),
            new ProfileRegistry(configuration.Profiles, configuration.SandboxRules, configuration.DefaultProfile));
    }
}
