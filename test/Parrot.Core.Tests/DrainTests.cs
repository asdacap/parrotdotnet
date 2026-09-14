using System.Collections.Concurrent;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Llm.Wire;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.Skills;
using Parrot.State;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

// The drain, driven through a provider that parks mid-call. Everything here
// depends on a turn still being in flight when the next prompt arrives, which
// is the whole of what a queue is for.
internal sealed class DrainTests : IDisposable
{
    private readonly string _blobDirectory = Path.Combine(
        Path.GetTempPath(), "parrot-drain-tests", Guid.NewGuid().ToString("n"));

    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly IEventBroker _broker = new EventBroker();
    private readonly List<AgentSessionDependencies> _dependencies = [];

    public void Dispose()
    {
        foreach (var dependencies in _dependencies)
        {
            dependencies.Dispose();
        }

        _broker.Dispose();
        _database.Dispose();

        if (Directory.Exists(_blobDirectory))
        {
            Directory.Delete(_blobDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Prompts_admitted_during_a_turn_are_answered_by_one_drain_in_order(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(Answer("first answer"), Answer("second answer"));
        var repository = new EventRepository(_database);
        await using var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);

        // Admitted while the first turn is parked inside the provider. The old
        // code started a second Run here, on the same history.
        _ = await session.Send([ConversationPart.TextPart("second prompt")], "msg-2", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);

        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();

        await session.Settled();
        await session.DisposeAsync();

        // Two calls, not four: one drain answered both.
        _ = await Assert.That(provider.Requests.Count).IsEqualTo(2);
        _ = await Assert.That(Prompts(provider.Requests[1])).IsEqualTo("first prompt | second prompt");
        _ = await Assert.That(Conversation(repository)).IsEqualTo(
            "user: first prompt | assistant: first answer | user: second prompt | assistant: second answer");
    }

    [Test]
    [Arguments(0)]
    [Arguments(100_000)]
    [Arguments(20_000)]
    public async Task Provider_output_budget_reserves_headroom_in_the_known_remaining_context(
        int contextWindow,
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(Answer("done"));
        var repository = new EventRepository(_database);
        await using var session = Session(provider, repository, [], contextWindow, 0, 0, 0, cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "message", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);

        var request = provider.Requests.Single();
        var estimatedInputTokens = Compactor.EstimateInputTokens(
            new ProviderModel(provider, new LLMModel(request.Model, provider.Id)),
            request.Instructions,
            request.Tools,
            request.Messages);
        var estimationHeadroom = (estimatedInputTokens + 19) / 20;
        var expectedMaximumOutputTokens = contextWindow > 0
            ? (int)Math.Min(32_768, contextWindow - estimatedInputTokens - estimationHeadroom)
            : 32_768;
        _ = await Assert.That(expectedMaximumOutputTokens).IsGreaterThan(0);
        _ = await Assert.That(request.MaxOutputTokens).IsEqualTo(expectedMaximumOutputTokens);
        if (contextWindow > 0)
        {
            _ = await Assert.That(estimatedInputTokens + request.MaxOutputTokens).IsLessThanOrEqualTo(contextWindow);
        }

        provider.Release();
        await session.Settled();
    }

    [Test]
    public async Task Provider_output_budget_uses_reported_input_usage_on_the_next_request(
        CancellationToken cancellationToken)
    {
        const int contextWindow = 180_000;
        const int reportedInputTokens = 147_776;
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", reportedInputTokens, 0, 1, "first answer", []),
            Answer("done"));
        var repository = new EventRepository(_database);
        await using var session = Session(provider, repository, [], contextWindow, 0, 0, 0, cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "message-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();

        _ = await session.Send([ConversationPart.TextPart("second prompt")], "message-2", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        var request = provider.Requests[1];
        _ = await Assert.That(request.MaxOutputTokens).IsGreaterThan(0);
        _ = await Assert.That(reportedInputTokens + request.MaxOutputTokens).IsLessThanOrEqualTo(contextWindow);
        _ = await Assert.That(request.MaxOutputTokens).IsLessThan(32_768);
        provider.Release();
        await session.Settled();
    }

    [Test]
    public async Task Maximum_input_fails_before_calling_provider_without_changing_reported_context(
        CancellationToken cancellationToken)
    {
        var provider = new ScriptedProvider("should not be called");
        var repository = new EventRepository(_database);
        await using var session = SessionWithInputLimit(
            provider,
            repository,
            contextWindow: 400_000,
            maximumInputTokens: 1,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "message", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await session.Settled();

        _ = await Assert.That(provider.Requests).IsEmpty();
        _ = await Assert.That(repository.Replay().Last(published =>
            published.PayloadCase == Event.PayloadOneofCase.TurnFailed).TurnFailed.Message)
            .Contains("input limit");
    }

    [Test]
    public async Task Exhausted_known_context_fails_before_calling_the_provider(
        CancellationToken cancellationToken)
    {
        var provider = new ScriptedProvider("should not be called");
        var repository = new EventRepository(_database);
        await using var session = Session(provider, repository, [], 1, 0, 0, 0, cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "message", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await session.Settled();

        _ = await Assert.That(provider.Requests).IsEmpty();
        _ = await Assert.That(repository.Replay().Last(published =>
            published.PayloadCase == Event.PayloadOneofCase.TurnFailed).TurnFailed.Message)
            .Contains("no output capacity");
    }

    [Test]
    public async Task Disposal_waits_for_a_blocked_drain_and_is_safe_when_repeated(
        CancellationToken cancellationToken)
    {
        var provider = new DisposalBlockedProvider();
        var repository = new EventRepository(_database);
        await using var session = Session(provider, repository, [], cancellationToken);

        try
        {
            _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
            await provider.WaitUntilEntered(cancellationToken);

            var firstDisposal = session.DisposeAsync().AsTask();
            var secondDisposal = session.DisposeAsync().AsTask();
            _ = await Assert.That(firstDisposal.IsCompleted).IsFalse();
            _ = await Assert.That(secondDisposal.IsCompleted).IsFalse();

            provider.Release();
            await Task.WhenAll(firstDisposal, secondDisposal);
            await session.DisposeAsync();
        }
        finally
        {
            provider.Release();
        }
    }

    [Test]
    public async Task A_queued_prompt_takes_a_turn_of_its_own_once_the_first_one_stops(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(Answer("first answer"), Answer("second answer"));
        var repository = new EventRepository(_database);
        await using var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await session.Send([ConversationPart.TextPart("queued prompt")], "msg-2", Delivery.Queue, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);

        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();

        await session.DisposeAsync();

        // A turn of its own, so two starts and two endings -- where a steer
        // would have joined the first turn and produced one of each.
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.TurnStarted)).IsEqualTo(2);
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.TurnEnded)).IsEqualTo(2);
        _ = await Assert.That(Prompts(provider.Requests[1])).IsEqualTo("first prompt | queued prompt");
    }

    [Test]
    public async Task Completion_callbacks_run_in_order_restart_after_retry_and_defer_terminal_events(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(Answer("retry candidate"), Answer("final candidate"));
        var repository = new EventRepository(_database);
        var calls = new List<string>();
        using var firstReservation = new TrackingDisposable();
        using var terminalReservation = new TrackingDisposable();
        var firstReservationWasDisposedBeforeSecondAttempt = false;
        var first = new RecordingCompletionCallback((candidate, invocation) =>
        {
            calls.Add($"first:{candidate.MessageId}");
            if (invocation == 1)
            {
                return AgentTurnCompletionOutcome.Continue(
                    firstReservation,
                    new PlanCompleted { Markdown = "discarded" });
            }

            firstReservationWasDisposedBeforeSecondAttempt = firstReservation.Disposed;
            return AgentTurnCompletionOutcome.Continue(
                terminalReservation,
                new PlanCompleted { Markdown = "retained" });
        });
        var second = new RecordingCompletionCallback((candidate, invocation) =>
        {
            calls.Add($"second:{candidate.MessageId}");
            return invocation == 1
                ? AgentTurnCompletionOutcome.Retry(
                    "retry",
                    false,
                    false,
                    false,
                    null)
                : AgentTurnCompletionOutcome.Continue(null, null);
        });
        var third = new RecordingCompletionCallback((candidate, _) =>
        {
            calls.Add($"third:{candidate.MessageId}");
            return AgentTurnCompletionOutcome.Continue(null, null);
        });
        var session = SessionWithCompletionCallbacks(
            provider,
            repository,
            [first, second, third],
            new TestProfileFixture().Mode,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "message", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();
        await session.DisposeAsync();

        var callbackNames = string.Join(',', calls.Select(call => call[..call.IndexOf(':')]));
        _ = await Assert.That(callbackNames).IsEqualTo("first,second,first,second,third");
        _ = await Assert.That(calls[0].Split(':')[1]).IsEqualTo(calls[1].Split(':')[1]);
        _ = await Assert.That(calls[0].Split(':')[1]).IsNotEqualTo(calls[2].Split(':')[1]);
        _ = await Assert.That(firstReservationWasDisposedBeforeSecondAttempt).IsTrue();
        _ = await Assert.That(firstReservation.Disposed).IsTrue();
        _ = await Assert.That(terminalReservation.Disposed).IsTrue();
        var plans = repository.Replay()
            .Where(published => published.PayloadCase == Event.PayloadOneofCase.PlanCompleted)
            .ToArray();
        _ = await Assert.That(plans).HasSingleItem();
        _ = await Assert.That(plans[0].PlanCompleted.Markdown).IsEqualTo("retained");
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.TurnStarted)).IsEqualTo(1);
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.TurnEnded)).IsEqualTo(1);
    }

    [Test]
    public async Task Send_and_wait_starts_after_the_predecessors_completion_callbacks(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer("first candidate"),
            Answer("first answer"),
            Answer("second answer"));
        var repository = new EventRepository(_database);
        var callback = new GatedCompletionCallback();
        await using var session = SessionWithCompletionCallbacks(
            provider,
            repository,
            [callback],
            new TestProfileFixture().Mode,
            cancellationToken);

        var first = session.SendAndWaitForResult("first prompt", cancellationToken);
        await provider.Arrived(cancellationToken);
        var second = session.SendAndWaitForResult("second prompt", cancellationToken);
        provider.Release();
        await callback.WaitUntilEntered(cancellationToken);

        _ = await Assert.That(provider.Requests).Count().IsEqualTo(1);
        _ = await Assert.That(first.IsCompleted).IsFalse();
        _ = await Assert.That(second.IsCompleted).IsFalse();

        callback.Release();
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(2);
        _ = await Assert.That(provider.Requests[1].Messages.Last(message => message.Role == LLMRole.System).Content)
            .IsEqualTo("retry first turn");
        _ = await Assert.That(second.IsCompleted).IsFalse();

        provider.Release();
        _ = await Assert.That(await first).IsEqualTo("first answer");
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests[2].Messages.Last(message => message.Role == LLMRole.User).Content)
            .IsEqualTo("second prompt");
        provider.Release();
        _ = await Assert.That(await second).IsEqualTo("second answer");
    }

    [Test]
    public async Task A_completion_callback_retry_resets_the_provider_request_budget(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(Answer("first candidate"), Answer("settled answer"));
        var repository = new EventRepository(_database);
        var retry = new RecordingCompletionCallback((candidate, invocation) => invocation == 1
            ? AgentTurnCompletionOutcome.Retry(
                "keep going",
                false,
                false,
                false,
                null)
            : AgentTurnCompletionOutcome.Continue(null, null));
        await using var session = SessionWithCompletionCallbacks(
            provider,
            repository,
            [retry],
            new DrainProfile(maxTurns: 2).Mode,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "message", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();
        await session.DisposeAsync();

        // Both requests are made and the turn completes. Without the reset the
        // retry would leave the budget at one, so the second request would be
        // treated as the final provider request and have tools withheld.
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(2);
        _ = await Assert.That(provider.Requests[1].Messages).DoesNotContain(message =>
            message.Role == LLMRole.System
            && message.Content.Contains("final provider request", StringComparison.Ordinal));
        _ = await Assert.That(Endings(repository)).IsEqualTo("stop");
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.TurnFailed)).IsEqualTo(0);
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.TurnEnded)).IsEqualTo(1);
    }

    [Test]
    public async Task Selected_skill_context_is_request_only_and_follows_the_active_turn(
        CancellationToken cancellationToken)
    {
        var skillRoot = Directory.CreateDirectory(Path.Combine(_blobDirectory, "skills"));
        var firstDirectory = Directory.CreateDirectory(Path.Combine(skillRoot.FullName, "first"));
        var secondDirectory = Directory.CreateDirectory(Path.Combine(skillRoot.FullName, "second"));
        var firstPath = Path.Combine(firstDirectory.FullName, "SKILL.md");
        var secondPath = Path.Combine(secondDirectory.FullName, "SKILL.md");
        await File.WriteAllTextAsync(firstPath, "---\nname: first\ndescription: first description\n---\nFIRST BODY", cancellationToken);
        await File.WriteAllTextAsync(secondPath, "---\nname: second\ndescription: second description\n---\nSECOND BODY", cancellationToken);
        var catalog = new SkillCatalog(
            [new(skillRoot.FullName, SkillScope.User, true)],
            () => (SkillConfiguration.Default, 0L));
        var skills = new AgentSkills(catalog, TestModels.PromptTemplates);
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "settled", "{}")),
            Answer("first answer"),
            Answer("second answer"));
        var repository = new EventRepository(_database);
        await using var session = SessionWithSkills(
            provider,
            repository,
            [new TestTool(new SettledTool("settled"))],
            new DrainProfile(maxTurns: 3).Mode,
            skills,
            null,
            cancellationToken);
        const string originalPrompt = "use $second then $second";
        const string steer = "also $first";

        _ = await session.Send([ConversationPart.TextPart(originalPrompt)], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await session.Send([ConversationPart.TextPart(steer)], "msg-2", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);

        var firstRequest = provider.Requests[0];
        var continuedRequest = provider.Requests[1];
        var firstSkillContext = firstRequest.Messages[^1];
        var continuedSkillContext = continuedRequest.Messages[^1];
        _ = await Assert.That(firstRequest.Instructions)
            .Contains("$first")
            .And.Contains("$second")
            .And.Contains(firstPath)
            .And.Contains(secondPath);
        _ = await Assert.That(firstSkillContext.Role).IsEqualTo(LLMRole.User);
        _ = await Assert.That(firstSkillContext.Content)
            .Contains("SECOND BODY")
            .And.DoesNotContain("FIRST BODY");
        _ = await Assert.That(firstSkillContext.Content.Split("SECOND BODY", StringSplitOptions.None).Length).IsEqualTo(2);
        _ = await Assert.That(continuedSkillContext.Content.IndexOf("SECOND BODY", StringComparison.Ordinal))
            .IsLessThan(continuedSkillContext.Content.IndexOf("FIRST BODY", StringComparison.Ordinal));
        _ = await Assert.That(continuedSkillContext.Content.Split("SECOND BODY", StringSplitOptions.None).Length).IsEqualTo(2);
        _ = await Assert.That(continuedSkillContext.Content.Split("FIRST BODY", StringSplitOptions.None).Length).IsEqualTo(2);
        _ = await Assert.That(repository.ModelHistory("agent"))
            .DoesNotContain(message => message.Content.Contains("<skill>", StringComparison.Ordinal));
        _ = await Assert.That(Conversation(repository)).IsEqualTo(
            $"user: {originalPrompt} | assistant:  | tool: settled | user: {steer}");

        provider.Release();
        await session.Settled();
        _ = await session.Send(
            [ConversationPart.TextPart("next turn $first")],
            "msg-3",
            Delivery.Steer,
            new IncomingActivity(IncomingActivityKind.Input, string.Empty),
            cancellationToken);
        await provider.Arrived(cancellationToken);
        var nextRequest = provider.Requests[2];
        _ = await Assert.That(nextRequest.Messages[^1].Content)
            .Contains("FIRST BODY")
            .And.DoesNotContain("SECOND BODY");
        provider.Release();
        await session.Settled();
        await session.DisposeAsync();

        var replay = repository.Replay().ToList();
        var loadedSkills = replay
            .Where(published => published.PayloadCase == Event.PayloadOneofCase.SkillLoaded)
            .ToArray();
        _ = await Assert.That(string.Join('|', loadedSkills.Select(published => published.SkillLoaded.Path)))
            .IsEqualTo($"{secondPath}|{firstPath}|{firstPath}");
        _ = await Assert.That(replay.IndexOf(loadedSkills[0]))
            .IsLessThan(replay.FindIndex(published => published.PayloadCase == Event.PayloadOneofCase.ToolStarted));
        _ = await Assert.That(repository.ModelHistory("agent"))
            .DoesNotContain(message => message.Content.Contains("<skill>", StringComparison.Ordinal));
        _ = await Assert.That(replay)
            .DoesNotContain(published => published.ToString().Contains("<skill>", StringComparison.Ordinal));
    }

    [Test]
    public async Task Harness_loads_selected_skills_independently_of_agent_security(CancellationToken cancellationToken)
    {
        var skillRoot = Directory.CreateDirectory(Path.Combine(_blobDirectory, "unavailable-skills"));
        var enabledDirectory = Directory.CreateDirectory(Path.Combine(skillRoot.FullName, "enabled"));
        var disabledDirectory = Directory.CreateDirectory(Path.Combine(skillRoot.FullName, "disabled"));
        var overBudgetDirectory = Directory.CreateDirectory(Path.Combine(skillRoot.FullName, "over-budget"));
        var enabledPath = Path.Combine(enabledDirectory.FullName, "SKILL.md");
        var disabledPath = Path.Combine(disabledDirectory.FullName, "SKILL.md");
        var overBudgetPath = Path.Combine(overBudgetDirectory.FullName, "SKILL.md");
        await File.WriteAllTextAsync(enabledPath, "---\nname: enabled\ndescription: enabled\n---\nENABLED BODY", cancellationToken);
        await File.WriteAllTextAsync(disabledPath, "---\nname: disabled\ndescription: disabled\n---\nDISABLED BODY", cancellationToken);
        const string overBudgetHeader = "---\nname: over-budget\ndescription: over budget\n---\n";
        await File.WriteAllTextAsync(
            overBudgetPath,
            overBudgetHeader + new string('x', (1024 * 1024) - overBudgetHeader.Length),
            cancellationToken);
        var catalog = new SkillCatalog(
            [new(skillRoot.FullName, SkillScope.User, true)],
            () => (new SkillConfiguration(true, [new(disabledPath, false)]), 0L));
        var skills = new AgentSkills(catalog, TestModels.PromptTemplates);
        var securityProfile = SecurityProfile.Compose(
            readOnly: true,
            [new SandboxRule(enabledPath, SandboxRuleAction.DenyRead)],
            [],
            []);
        var provider = new ScriptedProvider("done");
        var repository = new EventRepository(_database);
        await using var session = SessionWithSkills(
            provider,
            repository,
            [],
            new TestProfileFixture().Mode,
            skills,
            securityProfile,
            cancellationToken);

        _ = await session.Send(
            [ConversationPart.TextPart("$unknown $disabled $enabled $over-budget")],
            "msg",
            Delivery.Steer,
            new IncomingActivity(IncomingActivityKind.Input, string.Empty),
            cancellationToken);
        await session.Settled();
        await session.DisposeAsync();

        var request = provider.Requests.Single();
        _ = await Assert.That(request.Messages)
            .Contains(message => message.Content.Contains("ENABLED BODY", StringComparison.Ordinal))
            .And.DoesNotContain(message => message.Content.Contains("DISABLED BODY", StringComparison.Ordinal));
        _ = await Assert.That(repository.ModelHistory("agent"))
            .DoesNotContain(message => message.Content.Contains("BODY", StringComparison.Ordinal));
        _ = await Assert.That(repository.Replay().Count(
            published => published.PayloadCase == Event.PayloadOneofCase.SkillLoaded)).IsEqualTo(1);
    }

    [Test]
    public async Task A_drain_continues_until_every_queued_prompt_is_answered(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer("first answer"), Answer("second answer"), Answer("third answer"));
        var repository = new EventRepository(_database);
        await using var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await session.Send([ConversationPart.TextPart("second prompt")], "msg-2", Delivery.Queue, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        _ = await session.Send([ConversationPart.TextPart("third prompt")], "msg-3", Delivery.Queue, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);

        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();
        await session.DisposeAsync();

        _ = await Assert.That(provider.Requests.Count).IsEqualTo(3);
        _ = await Assert.That(repository.HasPendingInputs("agent")).IsFalse();
        _ = await Assert.That(Conversation(repository)).IsEqualTo(
            "user: first prompt | assistant: first answer | user: second prompt | assistant: second answer | "
            + "user: third prompt | assistant: third answer");
    }

    [Test]
    public async Task A_steer_admitted_during_a_tool_round_joins_the_turn_already_running(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "settled", "{}")), Answer("done"));
        var repository = new EventRepository(_database);
        await using var session = Session(
            provider,
            repository,
            [new TestTool(new SettledTool("settled"))],
            new DrainProfile(maxTurns: 2).Mode,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await session.Send([ConversationPart.TextPart("steer")], "msg-2", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);

        // The tool round settles and the turn carries on to its next boundary,
        // which is where the steer joins it.
        provider.Release();
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests[1].Tools).IsEmpty();
        _ = await Assert.That(provider.Requests[1].Messages).DoesNotContain(message =>
            message.Role == LLMRole.System
            && message.Content.Contains("Tool access is restored", StringComparison.Ordinal));
        provider.Release();
        await session.Settled();
        await session.DisposeAsync();

        // One turn, not two: the steer was answered inside the turn that was
        // already running rather than starting one of its own.
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.TurnStarted)).IsEqualTo(1);
        _ = await Assert.That(Conversation(repository))
            .IsEqualTo("user: first prompt | assistant:  | tool: settled | user: steer | "
                + "system: This is the final provider request allowed for the current turn. "
                + "Tools are unavailable except the settlement tools (wait, agent_send, interrupt_process, "
                + "agent_status, answer, question). Do not start new work. Settle anything still running if needed, "
                + "then provide the best possible final answer using the information already available. | assistant: done");
        _ = await Assert.That(Prompts(provider.Requests[1])).IsEqualTo("first prompt | steer");
        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:call-1:settled | finished:call-1:settled");
        _ = await Assert.That(repository.Replay().Single(published =>
            published.PayloadCase == Event.PayloadOneofCase.ToolFinished).ToolFinished.Result).IsEqualTo("settled");
    }

    [Test]
    public async Task Tool_finished_spills_only_oversized_utf8_results(CancellationToken cancellationToken)
    {
        var inline = new string('x', ToolOutputBlobStore.MaximumInlineBytes);
        var oversized = new string('界', 21_846);
        _ = await Assert.That(oversized.Length).IsLessThan(ToolOutputBlobStore.MaximumInlineBytes);
        _ = await Assert.That(System.Text.Encoding.UTF8.GetByteCount(oversized))
            .IsGreaterThan(ToolOutputBlobStore.MaximumInlineBytes);

        foreach (var result in new[] { string.Empty, inline, new string('x', ToolOutputBlobStore.MaximumInlineBytes + 1), oversized })
        {
            using var provider = new SteppedProvider(
                Answer(string.Empty, new LLMToolCall($"call-{result.Length}", "settled", "{}")), Answer("done"));
            var repository = new EventRepository(_database);
            await using var session = Session(provider, repository, [new TestTool(new SettledTool(result))], cancellationToken);

            _ = await session.Send([ConversationPart.TextPart("prompt")], $"msg-{result.Length}", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
            await provider.Arrived(cancellationToken);
            provider.Release();
            await provider.Arrived(cancellationToken);
            provider.Release();
            await session.DisposeAsync();

            var finished = repository.Replay().Last(published =>
                published.PayloadCase == Event.PayloadOneofCase.ToolFinished).ToolFinished;
            _ = await Assert.That(finished.HasResult).IsTrue();

            if (!ToolOutputBlobStore.IsOversized(result))
            {
                _ = await Assert.That(finished.Result).IsEqualTo(result);
                continue;
            }

            _ = await Assert.That(finished.Result).Contains("Tool output exceeded 64 KiB");
            _ = await Assert.That(finished.Result).DoesNotContain("界界界");
            var path = BlobPath(finished.Result);
            _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).IsEqualTo(result);
            var toolResult = provider.Requests[1].Messages.Last(message => message.Role == LLMRole.Tool);
            _ = await Assert.That(toolResult.Content).IsEqualTo(finished.Result);
        }
    }

    [Test]
    public async Task Statistics_replay_individual_facts_and_restore_tool_rounds(
        CancellationToken cancellationToken)
    {
        var repository = new EventRepository(_database);
        using (var firstProvider = new SteppedProvider(
            LLMEvent.Completed(
                "tool_calls", 10, 3, 4, string.Empty, [new LLMToolCall("call-1", "settled", "{}")]),
            LLMEvent.Completed("stop", 7, 2, 5, "first", [])))
        {
            await using var firstSession = Session(
                firstProvider,
                repository,
                [new TestTool(new SettledTool("settled"))],
                100_000,
                0.125,
                0.025,
                0.25,
                cancellationToken);
            _ = await firstSession.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
            await firstProvider.Arrived(cancellationToken);
            firstProvider.Release();
            await firstProvider.Arrived(cancellationToken);
            firstProvider.Release();
            await firstSession.DisposeAsync();
        }

        repository = new EventRepository(_database);
        using (var secondProvider = new SteppedProvider(
            LLMEvent.Completed("stop", 6, 1, 2, "second", [])))
        {
            await using var restoredSession = Session(secondProvider, repository, [], 100_000, 0.125, 0.025, 0.25, cancellationToken);
            _ = await restoredSession.Send([ConversationPart.TextPart("second prompt")], "msg-2", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
            await secondProvider.Arrived(cancellationToken);
            secondProvider.Release();
            await restoredSession.DisposeAsync();
        }

        var replay = repository.Replay().ToList();
        var statistics = replay
            .Where(published => published.PayloadCase == Event.PayloadOneofCase.RequestUsageRecorded)
            .Select(published => published.RequestUsageRecorded)
            .ToArray();
        var endings = replay
            .Where(published => published.PayloadCase == Event.PayloadOneofCase.TurnEnded)
            .Select(published => published.TurnEnded)
            .ToArray();

        _ = await Assert.That(statistics.Length).IsEqualTo(3);
        _ = await Assert.That(
            string.Join(" | ", statistics.Select(updated =>
                $"{updated.InputTokens}:{updated.CachedInputTokens}:{updated.OutputTokens}:"
                + $"{updated.ContextSize}:{updated.ContextLimit}")))
            .IsEqualTo("10:3:4:10:100000 | 7:2:5:7:100000 | 6:1:2:6:100000");
        _ = await Assert.That(statistics[0].InputCost).IsEqualTo(0.95);
        _ = await Assert.That(statistics[0].OutputCost).IsEqualTo(1.0);
        _ = await Assert.That(statistics[1].InputCost).IsEqualTo(0.675);
        _ = await Assert.That(statistics[1].OutputCost).IsEqualTo(1.25);
        _ = await Assert.That(statistics[2].InputCost).IsEqualTo(0.65);
        _ = await Assert.That(statistics[2].OutputCost).IsEqualTo(0.5);
        _ = await Assert.That(
            string.Join(" | ", endings.Select(ended => $"{ended.InputTokens}:{ended.OutputTokens}")))
            .IsEqualTo("17:9 | 23:11");
        _ = await Assert.That(replay.FindIndex(
            published => published.PayloadCase == Event.PayloadOneofCase.RequestUsageRecorded))
            .IsLessThan(replay.FindIndex(published => published.PayloadCase == Event.PayloadOneofCase.ToolStarted));
        var recovered = repository.ReplayStatistics().Agents["agent"];
        _ = await Assert.That(recovered.Self.Totals).IsEqualTo(new AgentUsageTotals(23, 6, 11, 1, 2.275, 2.75));
        _ = await Assert.That(repository.GetRuntimeStatistics().GetAgentStatistics("agent").Capture().Self.Totals).IsEqualTo(recovered.Self.Totals);
        _ = await Assert.That(replay.Any(published => published.PayloadCase == Event.PayloadOneofCase.AgentStatisticsUpdated)).IsFalse();
    }

    [Test]
    [Arguments("completed")]
    [Arguments("failed")]
    [Arguments("cancelled")]
    public async Task Provider_request_phases_are_transient_and_reset_on_retry_and_terminal_paths(
        string outcome, CancellationToken cancellationToken)
    {
        using var provider = new RequestPhaseProvider(
            outcome == "failed",
            [
                LLMEvent.HttpRequestStarted(),
                LLMEvent.HttpResponseHeadersReceived(),
                LLMEvent.Retry(1, TimeSpan.FromSeconds(1), "retry"),
                LLMEvent.HttpRequestStarted(),
                LLMEvent.HttpResponseHeadersReceived(),
            ]);
        var repository = new EventRepository(_database);
        using var subscription = _broker.Subscribe();
        await using var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        var published = new List<Event>();
        var expectedPhases = new[]
        {
            ProviderRequestPhase.Requesting,
            ProviderRequestPhase.HeadersReceived,
            ProviderRequestPhase.Idle,
            ProviderRequestPhase.Requesting,
            ProviderRequestPhase.HeadersReceived,
        };
        for (var phaseIndex = 0; phaseIndex < expectedPhases.Length; phaseIndex++)
        {
            var expectedPhase = expectedPhases[phaseIndex];
            await provider.Arrived(cancellationToken);
            while (subscription.Reader.TryRead(out var next))
            {
                published.Add(next);
            }

            _ = await Assert.That(published.Last(item =>
                item.PayloadCase == Event.PayloadOneofCase.ProviderRequestPhaseChanged)
                .ProviderRequestPhaseChanged.Phase).IsEqualTo(expectedPhase);
            if (expectedPhase is ProviderRequestPhase.Requesting or ProviderRequestPhase.HeadersReceived
                && published.All(item => item.PayloadCase != Event.PayloadOneofCase.RetryNotice))
            {
                _ = await Assert.That(session.Activity.Capture().LatestProviderActivityAge).IsNull();
                _ = await Assert.That(session.Activity.Capture().Recent).IsEmpty();
            }

            if (phaseIndex < expectedPhases.Length - 1)
            {
                provider.Release();
            }
        }

        if (outcome == "cancelled")
        {
            await session.Interrupt(cancellationToken);
        }
        else
        {
            provider.Release();
            await session.Settled();
        }

        if (outcome == "completed")
        {
            _ = await session.Send([ConversationPart.TextPart("next")], "msg-2", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
            for (var phaseIndex = 0; phaseIndex < expectedPhases.Length; phaseIndex++)
            {
                await provider.Arrived(cancellationToken);
                provider.Release();
            }

            await session.Settled();
        }

        await session.DisposeAsync();
        while (subscription.Reader.TryRead(out var next))
        {
            published.Add(next);
        }

        var phases = published.Where(item =>
            item.PayloadCase == Event.PayloadOneofCase.ProviderRequestPhaseChanged).ToArray();
        _ = await Assert.That(string.Join(" | ", phases.Select(item => item.ProviderRequestPhaseChanged.Phase)))
            .IsEqualTo(outcome == "completed"
                ? "Requesting | HeadersReceived | Idle | Requesting | HeadersReceived | Idle | Requesting | HeadersReceived | Idle | Requesting | HeadersReceived | Idle"
                : "Requesting | HeadersReceived | Idle | Requesting | HeadersReceived | Idle");
        _ = await Assert.That(string.Join(" | ", phases.Select(item => item.ProviderRequestPhaseChanged.Attempt)))
            .IsEqualTo(outcome == "completed" ? "1 | 1 | 1 | 2 | 2 | 2 | 1 | 1 | 1 | 2 | 2 | 2" : "1 | 1 | 1 | 2 | 2 | 2");
        _ = await Assert.That(phases.All(item => item.AgentSessionId == "agent" && item.Id.Length > 0)).IsTrue();
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.ProviderRequestPhaseChanged)).IsEqualTo(0);
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.RetryNotice)).IsEqualTo(outcome == "completed" ? 2 : 1);
        var retryIndex = published.FindIndex(item => item.PayloadCase == Event.PayloadOneofCase.RetryNotice);
        _ = await Assert.That(published[retryIndex - 1].ProviderRequestPhaseChanged.Phase)
            .IsEqualTo(ProviderRequestPhase.Idle);
        var terminalIndex = published.FindLastIndex(item => item.PayloadCase is
            Event.PayloadOneofCase.TurnEnded or Event.PayloadOneofCase.TurnFailed);
        _ = await Assert.That(published.IndexOf(phases[^1])).IsLessThan(terminalIndex);

        // Usage summaries are transient in the current design: statistics are
        // replayed from usage facts, so nothing is persisted for them.
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.AgentStatisticsUpdated)).IsEqualTo(0);
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.TurnFailed))
            .IsEqualTo(outcome == "failed" ? 1 : 0);
        if (outcome == "cancelled")
        {
            _ = await Assert.That(Endings(repository)).Contains("interrupted");
        }
    }

    [Test]
    [Arguments("text")]
    [Arguments("reasoning")]
    [Arguments("tool-id")]
    [Arguments("tool-name")]
    [Arguments("tool-arguments")]
    [Arguments("empty")]
    public async Task Provider_first_data_clears_header_phase_once_before_delta_and_resets_on_retry(
        string dataKind, CancellationToken cancellationToken)
    {
        var delta = dataKind switch
        {
            "text" => LLMEvent.TextDelta(" "),
            "reasoning" => LLMEvent.ReasoningDelta(" "),
            "tool-id" => LLMEvent.ToolCallDelta("call", string.Empty, string.Empty),
            "tool-name" => LLMEvent.ToolCallDelta(string.Empty, "tool", string.Empty),
            "tool-arguments" => LLMEvent.ToolCallDelta(string.Empty, string.Empty, "{"),
            _ => LLMEvent.TextDelta(string.Empty),
        };
        var clears = dataKind != "empty";
        (LLMEvent Event, int PhaseCount, ProviderRequestPhase Phase)[] steps =
        [
            (LLMEvent.HttpRequestStarted(), 1, ProviderRequestPhase.Requesting),
            (delta, 1, ProviderRequestPhase.Requesting),
            (LLMEvent.HttpResponseHeadersReceived(), 2, ProviderRequestPhase.HeadersReceived),
            (LLMEvent.TextDelta(string.Empty), 2, ProviderRequestPhase.HeadersReceived),
            (LLMEvent.ReasoningDelta(string.Empty, LLMReasoningKind.Summary, "part", completed: true), 2, ProviderRequestPhase.HeadersReceived),
            (LLMEvent.ToolCallDelta(string.Empty, string.Empty, string.Empty), 2, ProviderRequestPhase.HeadersReceived),
            (delta, clears ? 3 : 2, clears ? ProviderRequestPhase.Idle : ProviderRequestPhase.HeadersReceived),
            (delta, clears ? 3 : 2, clears ? ProviderRequestPhase.Idle : ProviderRequestPhase.HeadersReceived),
            (LLMEvent.Retry(1, TimeSpan.Zero, "retry"), clears ? 4 : 3, ProviderRequestPhase.Idle),
            (delta, clears ? 4 : 3, ProviderRequestPhase.Idle),
            (LLMEvent.HttpRequestStarted(), clears ? 5 : 4, ProviderRequestPhase.Requesting),
            (LLMEvent.HttpResponseHeadersReceived(), clears ? 6 : 5, ProviderRequestPhase.HeadersReceived),
            (delta, clears ? 7 : 5, clears ? ProviderRequestPhase.Idle : ProviderRequestPhase.HeadersReceived),
        ];
        using var provider = new RequestPhaseProvider(false, [.. steps.Select(step => step.Event)]);
        var repository = new EventRepository(_database);
        using var subscription = _broker.Subscribe();
        await using var session = Session(provider, repository, [], cancellationToken);
        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        var published = new List<Event>();
        var previousPhaseCount = 0;
        foreach (var step in steps)
        {
            await provider.Arrived(cancellationToken);
            while (subscription.Reader.TryRead(out var next))
            {
                published.Add(next);
            }

            var phases = published.Where(item => item.PayloadCase == Event.PayloadOneofCase.ProviderRequestPhaseChanged).ToArray();
            _ = await Assert.That(phases.Length).IsEqualTo(step.PhaseCount);
            _ = await Assert.That(phases[^1].ProviderRequestPhaseChanged.Phase).IsEqualTo(step.Phase);
            if (ReferenceEquals(step.Event, delta) && step.PhaseCount > previousPhaseCount)
            {
                _ = await Assert.That(published[^2]).IsEqualTo(phases[^1]);
                _ = await Assert.That(published[^1].PayloadCase).IsEqualTo(delta.Kind switch
                {
                    LLMEventKind.ReasoningDelta => Event.PayloadOneofCase.ReasoningChunk,
                    LLMEventKind.ToolCallDelta => Event.PayloadOneofCase.ToolCallChunk,
                    _ => Event.PayloadOneofCase.TextChunk,
                });
            }

            previousPhaseCount = step.PhaseCount;
            provider.Release();
        }

        await session.Settled();
        await session.DisposeAsync();
        while (subscription.Reader.TryRead(out var next))
        {
            published.Add(next);
        }

        var finalPhases = published.Where(item => item.PayloadCase == Event.PayloadOneofCase.ProviderRequestPhaseChanged).ToArray();
        _ = await Assert.That(finalPhases.Length).IsEqualTo(steps[^1].PhaseCount + 1);
        _ = await Assert.That(finalPhases[^1].ProviderRequestPhaseChanged.Phase).IsEqualTo(ProviderRequestPhase.Idle);
        _ = await Assert.That(finalPhases[^1].ProviderRequestPhaseChanged.Attempt).IsEqualTo(2u);
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.ProviderRequestPhaseChanged)).IsEqualTo(0);
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.TurnFailed)).IsEqualTo(0);
    }

    [Test]
    public async Task Completed_provider_calls_publish_live_usage_samples_after_durable_statistics(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed(
                "tool_calls", 10, 3, 4, string.Empty, [new LLMToolCall("call-1", "settled", "{}")]),
            LLMEvent.Completed("stop", -7, -2, -1, "done", []));
        var repository = new EventRepository(_database);
        using var subscription = _broker.Subscribe();
        await using var session = Session(
            provider,
            repository,
            [new TestTool(new SettledTool("settled"))],
            new DrainProfile(maxTurns: 2).Mode,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.DisposeAsync();

        var published = new List<Event>();
        while (subscription.Reader.TryRead(out var next))
        {
            published.Add(next);
        }

        var samples = published
            .Where(published => published.PayloadCase == Event.PayloadOneofCase.ProviderCallUsage)
            .ToArray();
        _ = await Assert.That(samples.Length).IsEqualTo(2);
        _ = await Assert.That(string.Join(" | ", samples.Select(sample =>
            $"{sample.AgentSessionId}:{sample.ProviderCallUsage.InputTokens}:{sample.ProviderCallUsage.OutputTokens}")))
            .IsEqualTo("agent:10:4 | agent:0:0");
        _ = await Assert.That(samples.All(sample => sample.Id.Length > 0)).IsTrue();
        _ = await Assert.That(repository.Replay().Any(published =>
            published.PayloadCase == Event.PayloadOneofCase.ProviderCallUsage)).IsFalse();

        foreach (var sample in samples)
        {
            var sampleIndex = published.IndexOf(sample);
            var statistics = published.Take(sampleIndex).Last(published =>
                published.PayloadCase == Event.PayloadOneofCase.RequestUsageRecorded);
            _ = await Assert.That(repository.Replay().Any(published => published.Id == statistics.Id)).IsTrue();
        }
    }

    [Test]
    public async Task Statistics_charge_cached_tokens_at_input_price_when_cache_price_is_omitted(
        CancellationToken cancellationToken)
    {
        var model = new LLMModel("model", "provider")
        {
            InputPrice = 0.125,
            Fields = ModelMetadataFields.InputPrice,
        };
        var statistics = new AgentStatistics(0, 0, 0, 0, 0, 0, 0)
            .Add(LLMEvent.Completed("stop", 10, 3, 0, string.Empty, []), model);

        _ = await Assert.That(statistics.InputCost).IsEqualTo(1.25);
        _ = await Assert.That(cancellationToken.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task Tool_instances_are_created_once_while_profile_filters_change_between_turns(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(Answer("first"), Answer("second"));
        var repository = new EventRepository(_database);
        var firstFactory = new CountingToolFactory("first");
        var secondFactory = new CountingToolFactory("second");
        var firstProfile = new DrainProfile(
            "first-profile",
            3,
            ["first"],
            new HashSet<string>(StringComparer.Ordinal),
            readOnly: false).Mode;
        var secondProfile = new DrainProfile(
            "second-profile",
            3,
            ["second"],
            new HashSet<string>(StringComparer.Ordinal),
            readOnly: true).Mode;
        await using var session = Session(
            provider,
            repository,
            [new TestTool(firstFactory.Tool, firstFactory), new TestTool(secondFactory.Tool, secondFactory)],
            firstProfile,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join(" | ", provider.Requests[0].Tools.Select(tool => tool.Name)))
            .IsEqualTo("first");
        provider.Release();
        await session.Settled();

        session.UpdateSelection(session.CurrentSelection().RequestedModel, secondProfile);
        _ = await session.Send([ConversationPart.TextPart("second prompt")], "msg-2", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join(" | ", provider.Requests[1].Tools.Select(tool => tool.Name)))
            .IsEqualTo("second");
        provider.Release();
        await session.Settled();

        _ = await Assert.That(firstFactory.CreateCount).IsEqualTo(1);
        _ = await Assert.That(secondFactory.CreateCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_session_tool_receives_live_security_at_each_invocation_without_recreation(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "record", "{}")),
            Answer("first"),
            Answer(string.Empty, new LLMToolCall("call-2", "record", "{}")),
            Answer("second"));
        var repository = new EventRepository(_database);
        var factory = new RecordingToolFactory();
        var writable = new DrainProfile(
            "writable",
            3,
            ["record"],
            new HashSet<string>(StringComparer.Ordinal),
            readOnly: false).Mode;
        var readOnly = new DrainProfile(
            "read-only",
            3,
            ["record"],
            new HashSet<string>(StringComparer.Ordinal),
            readOnly: true).Mode;
        await using var session = Session(provider, repository, [new TestTool(factory.Tool, factory)], writable, cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        session.UpdateSelection(session.CurrentSelection().RequestedModel, readOnly);
        provider.Release();
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join(" | ", factory.RecordingTool.Selections.Select(SelectionSummary)))
            .IsEqualTo("writable:True");
        provider.Release();
        await session.Settled();

        _ = await session.Send([ConversationPart.TextPart("second prompt")], "msg-2", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join(" | ", factory.RecordingTool.Selections.Select(SelectionSummary)))
            .IsEqualTo("writable:True | read-only:True");
        provider.Release();
        await session.Settled();

        _ = await Assert.That(factory.CreateCount).IsEqualTo(1);
    }

    [Test]
    public async Task Globally_disabled_tools_are_omitted_even_when_the_profile_allows_them(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-disabled", "settled", "{}")),
            Answer("done"));
        var repository = new EventRepository(_database);
        await using var session = Session(
            provider,
            repository,
            [new TestTool(new SettledTool("settled")), new TestTool(new HeldTool())],
            new DrainProfile(3, ["settled", "held"], new HashSet<string>(["settled"], StringComparer.Ordinal)).Mode,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join(" | ", provider.Requests[0].Tools.Select(tool => tool.Name)))
            .IsEqualTo("held");
        provider.Release();
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join(" | ", provider.Requests[1].Tools.Select(tool => tool.Name)))
            .IsEqualTo("held");
        provider.Release();
        await session.Settled();
        await session.DisposeAsync();

        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:call-disabled:settled | error:call-disabled:settled:unknown tool settled");
    }

    [Test]
    public async Task A_profile_turn_omits_tools_on_its_final_provider_request_and_resets_for_queued_input(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "settled", "{}")),
            Answer("first answer"),
            Answer("queued answer"));
        var repository = new EventRepository(_database);
        await using var session = Session(
            provider,
            repository,
            [new TestTool(new SettledTool("settled"))],
            new DrainProfile(maxTurns: 2).Mode,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await session.Send([ConversationPart.TextPart("queued prompt")], "msg-2", Delivery.Queue, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests[1].Tools).IsEmpty();
        _ = await Assert.That(provider.Requests[1].Messages).Contains(message =>
            message.Role == LLMRole.System
            && message.Content.Contains("final provider request", StringComparison.Ordinal));
        _ = await Assert.That(provider.Requests[1].Messages).DoesNotContain(message =>
            message.Role == LLMRole.System
            && message.Content.Contains("Tool access is restored", StringComparison.Ordinal));
        provider.Release();
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests[2].Tools).HasSingleItem();
        _ = await Assert.That(provider.Requests[2].Messages).Contains(message =>
            message.Role == LLMRole.System
            && message.Content.Contains("Tool access is restored", StringComparison.Ordinal));
        provider.Release();
        await session.Settled();
        await session.DisposeAsync();

        _ = await Assert.That(Endings(repository)).IsEqualTo("stop | stop");
        _ = await Assert.That(Conversation(repository)).IsEqualTo(
            "user: first prompt | assistant:  | tool: settled | "
            + "system: This is the final provider request allowed for the current turn. "
            + "Tools are unavailable except the settlement tools (wait, agent_send, interrupt_process, "
            + "agent_status, answer, question). Do not start new work. Settle anything still running if needed, "
            + "then provide the best possible final answer using the information already available. | "
            + "assistant: first answer | system: A new turn has started and its provider-request budget has reset. "
            + "Tool access is restored to the tools permitted for this turn. | "
            + "user: queued prompt | assistant: queued answer");
    }

    [Test]
    public async Task Tool_availability_is_restored_once_after_recovering_a_session(
        CancellationToken cancellationToken)
    {
        var repository = new EventRepository(_database);
        using (var firstProvider = new SteppedProvider(Answer("first answer")))
        {
            await using var firstSession = Session(firstProvider, repository, [], new DrainProfile(maxTurns: 1).Mode, cancellationToken);

            _ = await firstSession.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
            await firstProvider.Arrived(cancellationToken);
            firstProvider.Release();
            await firstSession.DisposeAsync();
        }

        using var restoredProvider = new SteppedProvider(Answer("second answer"), Answer("third answer"));
        await using var restoredSession = Session(
            restoredProvider,
            repository,
            [new TestTool(new SettledTool("settled"))],
            new DrainProfile(maxTurns: 2).Mode,
            cancellationToken);

        _ = await restoredSession.Send([ConversationPart.TextPart("second prompt")], "msg-2", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await restoredProvider.Arrived(cancellationToken);
        _ = await Assert.That(restoredProvider.Requests[0].Tools).HasSingleItem();
        _ = await Assert.That(restoredProvider.Requests[0].Messages.Count(message =>
            message.Role == LLMRole.System
            && message.Content.Contains("Tool access is restored", StringComparison.Ordinal))).IsEqualTo(1);
        restoredProvider.Release();
        await restoredSession.Settled();

        _ = await restoredSession.Send([ConversationPart.TextPart("third prompt")], "msg-3", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await restoredProvider.Arrived(cancellationToken);
        _ = await Assert.That(restoredProvider.Requests[1].Messages.Count(message =>
            message.Role == LLMRole.System
            && message.Content.Contains("Tool access is restored", StringComparison.Ordinal))).IsEqualTo(1);
        restoredProvider.Release();
        await restoredSession.Settled();

        _ = await Assert.That(repository.Messages("agent").Count(message =>
            message.Contains("Tool access is restored", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task Length_truncated_tool_call_is_discarded_and_reissued_before_execution(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed(
                "length",
                10,
                0,
                32_768,
                "partial",
                [new LLMToolCall("truncated", "settled", "{\"value\":\"")]),
            Answer(string.Empty, new LLMToolCall("complete", "settled", "{}")),
            Answer("done"));
        var repository = new EventRepository(_database);
        using var subscription = _broker.Subscribe();
        await using var session = Session(
            provider,
            repository,
            [new TestTool(new SettledTool("settled"))],
            new DrainProfile(maxTurns: 4).Mode,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "message", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);

        var correction = provider.Requests[1].Messages.Single(message =>
            message.Role == LLMRole.System
            && message.Content.Contains("cut off by the output limit", StringComparison.Ordinal));
        _ = await Assert.That(correction.Content).Contains("was not executed").And.Contains("shorter arguments");
        _ = await Assert.That(provider.Requests[1].Messages).DoesNotContain(message =>
            message.ToolCalls.Any(call => call.Id == "truncated"));
        _ = await Assert.That(repository.ModelHistory("agent")).DoesNotContain(message =>
            message.ToolCalls.Any(call => call.Id == "truncated"));
        _ = await Assert.That(ToolLifecycle(repository)).IsEmpty();

        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();

        var published = new List<Event>();
        while (subscription.Reader.TryRead(out var next))
        {
            published.Add(next);
        }

        var retry = published.Single(item => item.PayloadCase == Event.PayloadOneofCase.RetryNotice).RetryNotice;
        _ = await Assert.That(retry.Attempt).IsEqualTo(1);
        _ = await Assert.That(retry.RetryAfterMs).IsEqualTo(0);
        _ = await Assert.That(retry.Reason).Contains("cut off by the output limit");
        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:complete:settled | finished:complete:settled");
        _ = await Assert.That(Conversation(repository)).IsEqualTo(
            $"user: prompt | system: {correction.Content} | assistant:  | tool: settled | assistant: done");
    }

    [Test]
    public async Task Invalid_tool_call_arguments_are_discarded_and_reissued_before_execution(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed(
                "stop",
                10,
                0,
                32_768,
                "partial",
                [new LLMToolCall("invalid", "settled", "{\"value\":\"")]),
            Answer(string.Empty, new LLMToolCall("complete", "settled", "{}")),
            Answer("done"));
        var repository = new EventRepository(_database);
        using var subscription = _broker.Subscribe();
        await using var session = Session(
            provider,
            repository,
            [new TestTool(new SettledTool("settled"))],
            new DrainProfile(maxTurns: 4).Mode,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "message", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);

        var correction = provider.Requests[1].Messages.Single(message =>
            message.Role == LLMRole.System
            && message.Content.Contains("not valid JSON", StringComparison.Ordinal));
        _ = await Assert.That(correction.Content).Contains("was not executed").And.Contains("well-formed JSON arguments");
        _ = await Assert.That(provider.Requests[1].Messages).DoesNotContain(message =>
            message.ToolCalls.Any(call => call.Id == "invalid"));
        _ = await Assert.That(repository.ModelHistory("agent")).DoesNotContain(message =>
            message.ToolCalls.Any(call => call.Id == "invalid"));
        _ = await Assert.That(ToolLifecycle(repository)).DoesNotContain("invalid");

        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();

        var published = new List<Event>();
        while (subscription.Reader.TryRead(out var next))
        {
            published.Add(next);
        }

        var retry = published.Single(item => item.PayloadCase == Event.PayloadOneofCase.RetryNotice).RetryNotice;
        _ = await Assert.That(retry.Attempt).IsEqualTo(1);
        _ = await Assert.That(retry.RetryAfterMs).IsEqualTo(0);
        _ = await Assert.That(retry.Reason).Contains("not valid JSON");
        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:complete:settled | finished:complete:settled");
        _ = await Assert.That(Conversation(repository)).IsEqualTo(
            $"user: prompt | system: {correction.Content} | assistant:  | tool: settled | assistant: done");
    }

    [Test]
    public async Task Repeated_length_truncated_tool_calls_consume_the_provider_request_limit(
        CancellationToken cancellationToken)
    {
        var truncated = LLMEvent.Completed(
            "length",
            1,
            0,
            1,
            string.Empty,
            [new LLMToolCall("truncated", "settled", "{")]);
        using var provider = new SteppedProvider(truncated, truncated);
        var repository = new EventRepository(_database);
        await using var session = Session(
            provider,
            repository,
            [new TestTool(new SettledTool("settled"))],
            new DrainProfile(maxTurns: 2).Mode,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "message", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();

        _ = await Assert.That(provider.Requests).Count().IsEqualTo(2);
        _ = await Assert.That(ToolLifecycle(repository)).IsEmpty();
        _ = await Assert.That(repository.ModelHistory("agent")).DoesNotContain(message => message.ToolCalls.Count > 0);
        _ = await Assert.That(repository.Replay().Last(published =>
            published.PayloadCase == Event.PayloadOneofCase.TurnFailed).TurnFailed.Message)
            .IsEqualTo("the turn exceeded its provider-request limit");
    }

    [Test]
    public async Task Length_completion_without_tool_calls_keeps_the_normal_completion_behavior(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("length", 1, 0, 32_768, "partial answer", []));
        var repository = new EventRepository(_database);
        await using var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "message", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();

        _ = await Assert.That(provider.Requests).Count().IsEqualTo(1);
        _ = await Assert.That(Endings(repository)).IsEqualTo("length");
        _ = await Assert.That(Conversation(repository)).IsEqualTo("user: prompt | assistant: partial answer");
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.RetryNotice)).IsEqualTo(0);
    }

    [Test]
    public async Task A_tool_call_returned_after_tools_are_omitted_is_settled_and_grants_one_more_final_request(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "settled", "{}")),
            Answer("final answer"));
        var repository = new EventRepository(_database);
        await using var session = Session(
            provider,
            repository,
            [new TestTool(new SettledTool("settled"))],
            new DrainProfile(maxTurns: 1).Mode,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests.Single().Tools).IsEmpty();
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();
        await session.DisposeAsync();

        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:call-1:settled | error:call-1:settled:unknown tool settled");
        _ = await Assert.That(Endings(repository)).IsEqualTo("stop");
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.TurnFailed)).IsEqualTo(0);
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.TurnEnded)).IsEqualTo(1);
    }

    [Test]
    public async Task A_budget_exhausted_final_request_carries_only_settlement_tools(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "survivor", "{}")),
            Answer(string.Empty, new LLMToolCall("call-2", "worker", "{}")),
            Answer("final answer"));
        var repository = new EventRepository(_database);
        await using var session = Session(
            provider,
            repository,
            [
                new TestTool(new SurvivingTool("survivor")),
                new TestTool(new SettledTool("worker")),
            ],
            new DrainProfile(maxTurns: 2).Mode,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        var finalDefinitions = provider.Requests[1].Tools.Select(definition => definition.Name).ToArray();
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();
        await session.DisposeAsync();

        _ = await Assert.That(finalDefinitions).HasSingleItem();
        _ = await Assert.That(finalDefinitions.Single()).IsEqualTo("survivor");
        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:call-1:survivor | finished:call-1:survivor | "
            + "started:call-2:worker | error:call-2:worker:unknown tool worker");
        _ = await Assert.That(Endings(repository)).IsEqualTo("stop");
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.TurnFailed)).IsEqualTo(0);
    }

    [Test]
    public async Task Settlement_extensions_are_capped_and_then_the_turn_fails_as_a_runaway(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "survivor", "{}")),
            Answer(string.Empty, new LLMToolCall("call-2", "survivor", "{}")),
            Answer(string.Empty, new LLMToolCall("call-3", "survivor", "{}")),
            Answer(string.Empty, new LLMToolCall("call-4", "survivor", "{}")),
            Answer(string.Empty, new LLMToolCall("call-5", "survivor", "{}")),
            Answer(string.Empty, new LLMToolCall("call-6", "survivor", "{}")),
            Answer("never reached"));
        var repository = new EventRepository(_database);
        await using var session = Session(
            provider,
            repository,
            [new TestTool(new SurvivingTool("survivor"))],
            new DrainProfile(maxTurns: 2).Mode,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        for (var cycle = 0; cycle < 6; cycle++)
        {
            await provider.Arrived(cancellationToken);
            provider.Release();
        }

        await session.Settled();
        await session.DisposeAsync();

        _ = await Assert.That(provider.Requests).Count().IsEqualTo(6);
        _ = await Assert.That(repository.Replay().Last(published =>
            published.PayloadCase == Event.PayloadOneofCase.TurnFailed).TurnFailed.Message)
            .IsEqualTo("the turn exceeded its provider-request limit");
    }

    [Test]
    public async Task Incomplete_tool_documentation_fails_before_calling_the_provider(
        CancellationToken cancellationToken)
    {
        var provider = new ScriptedProvider("should not be called");
        var repository = new EventRepository(_database);
        await using var session = Session(
            provider,
            repository,
            [new TestTool(new SettledTool("settled"))],
            TestModels.EmptyToolDefinitions,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await session.DisposeAsync();

        _ = await Assert.That(repository.Replay().Last(published =>
            published.PayloadCase == Event.PayloadOneofCase.TurnFailed).TurnFailed.Message)
            .Contains("tools.settled is not defined");
        _ = await Assert.That(provider.Requests).IsEmpty();
    }

    [Test]
    public async Task Terminal_provider_failures_publish_the_response_body(CancellationToken cancellationToken)
    {
        var provider = new FailingProvider(new ProviderHttpException(400, "invalid_request", "bad", "broken", "provider body"));
        var repository = new EventRepository(_database);
        await using var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await session.Settled();
        await session.DisposeAsync();

        var failure = repository.Replay().Last(published =>
            published.PayloadCase == Event.PayloadOneofCase.TurnFailed).TurnFailed;
        _ = await Assert.That(failure.Message).Contains("HTTP 400");
        _ = await Assert.That(failure.ProviderResponseBody).IsEqualTo("provider body");
    }

    [Test]
    [Timeout(90_000)]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(5, false)]
    [Arguments(5, true)]
    public async Task Header_retry_exhaustion_reaches_the_terminal_event_without_private_details(
        int maximumRetries, bool cancelled, CancellationToken cancellationToken)
    {
        var failingProvider = new FailingProvider(cancelled
            ? new OperationCanceledException("private-sentinel")
            : new HeaderTimeoutException("private-sentinel"));
        ILLMProvider provider = maximumRetries == 5
            ? new RetryingProvider(failingProvider)
            : new RetryingProvider(failingProvider) { HeaderTimeoutMaxRetries = maximumRetries };
        var repository = new EventRepository(_database);
        await using var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await session.Settled();
        await session.DisposeAsync();

        var events = repository.Replay().ToArray();
        _ = await Assert.That(events.Count(published => published.PayloadCase == Event.PayloadOneofCase.RetryNotice))
            .IsEqualTo(cancelled ? 0 : maximumRetries);
        var terminal = events.Last(published => published.PayloadCase is
            Event.PayloadOneofCase.TurnFailed or Event.PayloadOneofCase.TurnEnded);
        if (cancelled)
        {
            _ = await Assert.That(terminal.PayloadCase).IsEqualTo(Event.PayloadOneofCase.TurnEnded);
            _ = await Assert.That(events.Any(published => published.PayloadCase == Event.PayloadOneofCase.TurnFailed)).IsFalse();
        }
        else
        {
            _ = await Assert.That(terminal.PayloadCase).IsEqualTo(Event.PayloadOneofCase.TurnFailed);
            _ = await Assert.That(terminal.TurnFailed.Message).IsEqualTo(
                $"Provider header timeout retry limit exceeded ({maximumRetries} retries, {maximumRetries + 1} attempts).");
            _ = await Assert.That(terminal.TurnFailed.ProviderResponseBody).IsEmpty();
            _ = await Assert.That(terminal.TurnFailed.Message).DoesNotContain("private-sentinel");
        }
    }

    [Test]
    public async Task An_unknown_tool_emits_an_error_before_the_turn_continues(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "missing", "{}")), Answer("done"));
        var repository = new EventRepository(_database);
        await using var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.DisposeAsync();

        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:call-1:missing | error:call-1:missing:unknown tool missing");
        _ = await Assert.That(session.CaptureStatistics().Self.Totals.ToolCalls).IsEqualTo(0);
        _ = await Assert.That(repository.ReplayStatistics().Agents["agent"].Self.Totals.ToolCalls).IsEqualTo(0);
    }

    [Test]
    public async Task A_settled_tool_is_cleared_before_the_next_provider_request(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer("tool preface", new LLMToolCall("call-1", "settled", "{}")), Answer("done"));
        var repository = new EventRepository(_database);
        await using var session = Session(
            provider,
            repository,
            [new TestTool(new SettledTool("result"))],
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);

        var executing = session.Activity.Capture();
        _ = await Assert.That(executing.CurrentTool).IsNull();
        _ = await Assert.That(executing.Recent).Count().IsEqualTo(1);
        _ = await Assert.That(executing.Recent[0].Content).IsEqualTo("tool preface");
        provider.Release();
        await session.Settled();
        await session.DisposeAsync();
        var settled = session.Activity.Capture();
        _ = await Assert.That(settled.CurrentTool).IsNull();
        _ = await Assert.That(string.Join(',', settled.Recent.Select(static entry => entry.Content)))
            .IsEqualTo("tool preface,done");
    }

    [Test]
    public async Task A_failing_tool_is_cleared_before_the_next_provider_request(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "failure", "{}")), Answer("done"));
        var repository = new EventRepository(_database);
        await using var session = Session(
            provider,
            repository,
            [new TestTool(new FailureTool())],
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);

        _ = await Assert.That(session.Activity.Capture().CurrentTool).IsNull();
        provider.Release();
        await session.DisposeAsync();
        _ = await Assert.That(session.Activity.Capture().CurrentTool).IsNull();
        _ = await Assert.That(session.CaptureStatistics().Self.Totals.ToolCalls).IsEqualTo(1);
        _ = await Assert.That(repository.ReplayStatistics().Agents["agent"].Self.Totals.ToolCalls).IsEqualTo(1);
    }

    [Test]
    public async Task An_unknown_tool_never_appears_as_current(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "missing", "{}")), Answer("done"));
        var repository = new EventRepository(_database);
        await using var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);

        _ = await Assert.That(session.Activity.Capture().CurrentTool).IsNull();
        provider.Release();
        await session.DisposeAsync();
    }

    [Test]
    public async Task An_interrupt_ends_the_turn_leaving_every_tool_call_answered(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "held", "{}"), new LLMToolCall("call-2", "held", "{}")),
            Answer("after the interrupt"));
        var repository = new EventRepository(_database);
        var heldTool = new HeldTool();
        await using var session = Session(provider, repository, [new TestTool(heldTool)], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await heldTool.Started.WaitAsync(cancellationToken);
        _ = await Assert.That(session.Activity.Capture().CurrentTool).IsEqualTo("held");

        await session.Interrupt(cancellationToken);

        _ = await Assert.That(session.IsIdle()).IsTrue();
        _ = await Assert.That(session.Activity.Capture().CurrentTool).IsNull();
        _ = await Assert.That(Endings(repository)).Contains("interrupted");

        // The next prompt is what proves it: a provider rejects a history
        // holding a call with no result, so this call is the assertion.
        _ = await session.Send([ConversationPart.TextPart("second prompt")], "msg-2", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.DisposeAsync();

        var answered = string.Join(" | ", provider.Requests[1].Messages
            .Where(message => message.Role == LLMRole.Tool)
            .Select(message => message.ToolCallId));

        _ = await Assert.That(answered).IsEqualTo("call-1 | call-2");
        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:call-1:held | cancelled:call-1:held | cancelled:call-2:held");
        _ = await Assert.That(session.CaptureStatistics().Self.Totals.ToolCalls).IsEqualTo(1);
        _ = await Assert.That(repository.ReplayStatistics().Agents["agent"].Self.Totals.ToolCalls).IsEqualTo(1);
    }

    [Test]
    public async Task An_interrupt_resumes_the_drain_for_input_that_was_still_pending(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(Answer("first answer"), Answer("queued answer"));
        var repository = new EventRepository(_database);
        await using var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);

        // Admitted but never promoted: the turn it would have joined is the
        // one being stopped.
        _ = await session.Send([ConversationPart.TextPart("queued prompt")], "msg-2", Delivery.Queue, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);

        await session.Interrupt(cancellationToken);

        // Nothing was admitted after the interrupt, so a second provider call
        // can only be the drain resuming for what was left pending.
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.DisposeAsync();

        _ = await Assert.That(repository.HasPendingInputs("agent")).IsFalse();
        _ = await Assert.That(Prompts(provider.Requests[1])).IsEqualTo("first prompt | queued prompt");
    }

    [Test]
    public async Task Consecutive_parallel_safe_calls_settle_and_remain_in_provider_order(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(
                string.Empty,
                new LLMToolCall("call-1", "parallel", "{}"),
                new LLMToolCall("call-2", "parallel", "{}"),
                new LLMToolCall("call-3", "parallel", "{}")),
            Answer("done"));
        var repository = new EventRepository(_database);
        var tool = new GatedTool("parallel", parallelSafe: true);
        await using var session = Session(provider, repository, [new TestTool(tool)], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await tool.Started("call-1", cancellationToken);
        await tool.Started("call-2", cancellationToken);
        await tool.Started("call-3", cancellationToken);
        _ = await Assert.That(tool.MaximumActive).IsEqualTo(3);

        tool.Release("call-3");
        await tool.Finished("call-3", cancellationToken);
        tool.Release("call-2");
        await tool.Finished("call-2", cancellationToken);
        tool.Release("call-1");
        await tool.Finished("call-1", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.DisposeAsync();

        _ = await Assert.That(string.Join(" | ", repository.ToolTerminals("agent").Select(terminal => terminal.ToolCallId)))
            .IsEqualTo("call-1 | call-2 | call-3");
        _ = await Assert.That(string.Join(" | ", provider.Requests[1].Messages
            .Where(message => message.Role == LLMRole.Tool)
            .Select(message => $"{message.ToolCallId}:{message.Content}")))
            .IsEqualTo("call-1:call-1 | call-2:call-2 | call-3:call-3");
        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:call-1:parallel | started:call-2:parallel | started:call-3:parallel | "
            + "finished:call-1:parallel | finished:call-2:parallel | finished:call-3:parallel");
    }

    [Test]
    public async Task Parallel_safe_runs_stop_at_unsafe_calls_and_resume_after_each_barrier(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(
                string.Empty,
                new LLMToolCall("safe-1", "safe", "{}"),
                new LLMToolCall("safe-2", "safe", "{}"),
                new LLMToolCall("unsafe", "unsafe", "{}"),
                new LLMToolCall("safe-3", "safe", "{}"),
                new LLMToolCall("safe-4", "safe", "{}")),
            Answer("done"));
        var repository = new EventRepository(_database);
        var safe = new GatedTool("safe", parallelSafe: true);
        var unsafeTool = new GatedTool("unsafe", parallelSafe: false);
        await using var session = Session(
            provider,
            repository,
            [new TestTool(safe), new TestTool(unsafeTool)],
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await safe.Started("safe-1", cancellationToken);
        await safe.Started("safe-2", cancellationToken);
        _ = await Assert.That(safe.MaximumActive).IsEqualTo(2);
        _ = await Assert.That(unsafeTool.HasStarted("unsafe")).IsFalse();
        _ = await Assert.That(safe.HasStarted("safe-3")).IsFalse();

        safe.Release("safe-1");
        safe.Release("safe-2");
        await safe.Finished("safe-1", cancellationToken);
        await safe.Finished("safe-2", cancellationToken);
        await unsafeTool.Started("unsafe", cancellationToken);
        _ = await Assert.That(safe.HasStarted("safe-3")).IsFalse();

        unsafeTool.Release("unsafe");
        await unsafeTool.Finished("unsafe", cancellationToken);
        await safe.Started("safe-3", cancellationToken);
        await safe.Started("safe-4", cancellationToken);
        safe.Release("safe-3");
        safe.Release("safe-4");
        await safe.Finished("safe-3", cancellationToken);
        await safe.Finished("safe-4", cancellationToken);

        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.DisposeAsync();

        _ = await Assert.That(string.Join(" | ", provider.Requests[1].Messages
            .Where(message => message.Role == LLMRole.Tool)
            .Select(message => message.ToolCallId)))
            .IsEqualTo("safe-1 | safe-2 | unsafe | safe-3 | safe-4");
        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:safe-1:safe | started:safe-2:safe | finished:safe-1:safe | "
            + "finished:safe-2:safe | started:unsafe:unsafe | finished:unsafe:unsafe | "
            + "started:safe-3:safe | started:safe-4:safe | finished:safe-3:safe | finished:safe-4:safe");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task Image_budget_rejects_the_rest_of_a_batch_but_resets_for_the_next_cycle(
        int spareBytes,
        CancellationToken cancellationToken)
    {
        var imageBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        _ = Directory.CreateDirectory(_blobDirectory);
        await File.WriteAllBytesAsync(Path.Combine(_blobDirectory, "pixel.png"), imageBytes, cancellationToken);
        var resources = new UserSessionResources(
            new StatePaths(_blobDirectory, _blobDirectory, _blobDirectory),
            UserSessionId.Parse("images"),
            ProjectWorkspace.FromLaunchDirectory(_blobDirectory));
        var imageStore = new ImageArtifactStore(resources);
        var repository = new EventRepository(_database, imageStore);
        var images = new ImageArtifactRepository(imageStore, repository);
        using var provider = new SteppedProvider(
            Answer(
                string.Empty,
                new LLMToolCall("accepted", "read_image", "{\"path\":\"pixel.png\"}"),
                new LLMToolCall("overflow", "read_image", "{\"path\":\"pixel.png\"}"),
                new LLMToolCall("latched", "read_image", "{\"path\":\"missing.png\"}"),
                new LLMToolCall("text", "settled", "{}")),
            Answer(string.Empty, new LLMToolCall("retry", "read_image", "{\"path\":\"pixel.png\"}")),
            Answer("done"));
        await using var session = SessionWithImageLimit(
            provider,
            repository,
            [new TestTool(new ReadImageTool(new ToolWorkspace(_blobDirectory), images)), new TestTool(new SettledTool("continued"))],
            imageBytes.Length + spareBytes,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "message", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests[1].Messages.SelectMany(message => message.Contents)
            .Count(content => content.Kind == LLMContentKind.Image)).IsEqualTo(1);
        foreach (var rejectedCallId in new[] { "overflow", "latched" })
        {
            var feedback = provider.Requests[1].Messages.Single(message => message.ToolCallId == rejectedCallId);
            _ = await Assert.That(feedback.Content).Contains("Retry in a new tool-call cycle.");
        }

        provider.Release();
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests[2].Messages.SelectMany(message => message.Contents)
            .Count(content => content.Kind == LLMContentKind.Image)).IsEqualTo(2);
        _ = await Assert.That(provider.Requests[2].Messages.Single(message => message.ToolCallId == "retry").Content)
            .IsEqualTo("image read");
        provider.Release();
        await session.Settled();

        var terminals = repository.ToolTerminals("agent");
        _ = await Assert.That(string.Join(" | ", terminals.Select(terminal => $"{terminal.ToolCallId}:{terminal.Status}")))
            .IsEqualTo("accepted:Finished | overflow:ImageBudgetExceeded | latched:ImageBudgetExceeded | text:Finished | retry:Finished");
        _ = await Assert.That(terminals.Where(terminal => terminal.Status == ToolExecutionStatus.ImageBudgetExceeded)
            .SelectMany(terminal => terminal.ResultParts)).DoesNotContain(part => part.Kind == ConversationPartKind.ImageArtifact);
        _ = await Assert.That(repository.Replay().Count(published => published.PayloadCase == Event.PayloadOneofCase.ToolError))
            .IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Restored_image_batches_reconstruct_accepted_bytes_and_the_overflow_latch(
        bool overflowSettled,
        CancellationToken cancellationToken)
    {
        var imageBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        _ = Directory.CreateDirectory(_blobDirectory);
        await File.WriteAllBytesAsync(Path.Combine(_blobDirectory, "pixel.png"), imageBytes, cancellationToken);
        var resources = new UserSessionResources(
            new StatePaths(_blobDirectory, _blobDirectory, _blobDirectory),
            UserSessionId.Parse("images"),
            ProjectWorkspace.FromLaunchDirectory(_blobDirectory));
        var imageStore = new ImageArtifactStore(resources);
        var repository = new EventRepository(_database, imageStore);
        var images = new ImageArtifactRepository(imageStore, repository);
        using var source = new MemoryStream(imageBytes);
        var artifact = await images.Persist(source, "upload", "pixel.png", "read_image", cancellationToken);
        repository.AppendConversation(
            new Event { Id = "assistant", AgentSessionId = "agent" },
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart(string.Empty)],
            [new LLMToolCall("accepted", "read_image", "{\"path\":\"pixel.png\"}"),
                new LLMToolCall("overflow", "read_image", "{\"path\":\"pixel.png\"}"),
                new LLMToolCall("remaining", "read_image", "{\"path\":\"pixel.png\"}"),
                new LLMToolCall("text", "settled", "{}")],
            string.Empty);
        var sequence = repository.Conversation("agent").Single().Sequence;
        _ = repository.AppendToolSettlement(
            new Event { Id = "accepted-result", AgentSessionId = "agent" },
            sequence,
            new ToolExecutionTerminal("accepted", "read_image", ToolExecutionStatus.Finished, [ConversationPart.TextPart("image read"), ConversationPart.ImageArtifact(artifact)], "image read"));
        if (overflowSettled)
        {
            _ = repository.AppendToolSettlement(
                new Event { Id = "overflow-result", AgentSessionId = "agent" },
                sequence,
                new ToolExecutionTerminal("overflow", "read_image", ToolExecutionStatus.ImageBudgetExceeded, [ConversationPart.TextPart("opaque persisted failure")], "opaque persisted failure"));
        }

        using var provider = new SteppedProvider(Answer("done"));
        await using var session = SessionWithImageLimit(
            provider,
            repository,
            [new TestTool(new ReadImageTool(new ToolWorkspace(_blobDirectory), images)), new TestTool(new SettledTool("continued"))],
            imageBytes.Length * (overflowSettled ? 3 : 1),
            cancellationToken);
        _ = await session.Send([ConversationPart.TextPart("resume")], "message", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests.Single().Messages.SelectMany(message => message.Contents)
            .Count(content => content.Kind == LLMContentKind.Image)).IsEqualTo(1);
        provider.Release();
        await session.Settled();

        var terminals = repository.ToolTerminals("agent");
        _ = await Assert.That(string.Join(" | ", terminals.Select(terminal => $"{terminal.ToolCallId}:{terminal.Status}")))
            .IsEqualTo("accepted:Finished | overflow:ImageBudgetExceeded | remaining:ImageBudgetExceeded | text:Finished");
        _ = await Assert.That(terminals.Where(terminal => terminal.Status == ToolExecutionStatus.ImageBudgetExceeded)
            .SelectMany(terminal => terminal.ResultParts)).DoesNotContain(part => part.Kind == ConversationPartKind.ImageArtifact);
        _ = await Assert.That(repository.Replay().Where(published => published.PayloadCase == Event.PayloadOneofCase.ToolStarted))
            .DoesNotContain(published => published.ToolStarted.ToolCallId == "accepted"
                || (overflowSettled && published.ToolStarted.ToolCallId == "overflow"));
    }

    [Test]
    public async Task Restored_batches_skip_settled_calls_and_reconcile_remaining_safe_calls(
        CancellationToken cancellationToken)
    {
        var repository = new EventRepository(_database);
        var calls = new[]
        {
            new LLMToolCall("settled", "parallel", "{}"),
            new LLMToolCall("safe-1", "parallel", "{}"),
            new LLMToolCall("safe-2", "parallel", "{}"),
            new LLMToolCall("unknown", "missing", "{}"),
        };
        repository.AppendConversation(
            new Event { Id = "assistant", AgentSessionId = "agent" },
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart(string.Empty)],
            calls,
            string.Empty);
        var sequence = repository.Conversation("agent").Single().Sequence;
        _ = repository.AppendToolSettlement(
            new Event { Id = "settled-result", AgentSessionId = "agent" },
            sequence,
            new ToolExecutionTerminal(
                "settled",
                "parallel",
                ToolExecutionStatus.Finished,
                [ConversationPart.TextPart("already settled")],
                "already settled"));

        using var provider = new SteppedProvider(Answer("done"));
        var tool = new GatedTool("parallel", parallelSafe: true);
        await using var session = Session(provider, repository, [new TestTool(tool)], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await tool.Started("safe-1", cancellationToken);
        await tool.Started("safe-2", cancellationToken);
        _ = await Assert.That(tool.MaximumActive).IsEqualTo(2);
        _ = await Assert.That(tool.HasStarted("unknown")).IsFalse();
        tool.Release("safe-1");
        tool.Release("safe-2");
        await tool.Finished("safe-1", cancellationToken);
        await tool.Finished("safe-2", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.DisposeAsync();

        _ = await Assert.That(repository.ToolTerminals("agent")).Count().IsEqualTo(4);
        _ = await Assert.That(repository.ToolTerminals("agent").First(terminal =>
            terminal.ToolCallId == "settled").Status).IsEqualTo(ToolExecutionStatus.Finished);
        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:safe-1:parallel | started:safe-2:parallel | finished:safe-1:parallel | "
            + "finished:safe-2:parallel | started:unknown:missing | error:unknown:missing:unknown tool missing");
        _ = await Assert.That(string.Join(" | ", provider.Requests.Single().Messages
            .Where(message => message.Role == LLMRole.Tool)
            .Select(message => message.ToolCallId)))
            .IsEqualTo("settled | safe-1 | safe-2 | unknown");
    }

    [Test]
    public async Task Activity_keeps_an_overlapping_tool_visible_until_the_last_one_finishes(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(
                string.Empty,
                new LLMToolCall("call-1", "parallel", "{}"),
                new LLMToolCall("call-2", "parallel", "{}")),
            Answer("done"));
        var repository = new EventRepository(_database);
        var tool = new GatedTool("parallel", parallelSafe: true);
        await using var session = Session(provider, repository, [new TestTool(tool)], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await tool.Started("call-1", cancellationToken);
        await tool.Started("call-2", cancellationToken);
        _ = await Assert.That(session.Activity.Capture().CurrentTool).IsNotNull();

        tool.Release("call-1");
        await tool.Finished("call-1", cancellationToken);
        _ = await Assert.That(session.Activity.Capture().CurrentTool).IsNotNull();
        tool.Release("call-2");
        await tool.Finished("call-2", cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(session.Activity.Capture().CurrentTool).IsNull();
        provider.Release();
        await session.DisposeAsync();
    }

    [Test]
    public async Task Interrupt_settles_started_parallel_calls_and_cancels_later_calls_before_next_prompt(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(
                string.Empty,
                new LLMToolCall("safe-1", "safe", "{}"),
                new LLMToolCall("safe-2", "safe", "{}"),
                new LLMToolCall("unsafe", "unsafe", "{}"),
                new LLMToolCall("unstarted", "safe", "{}")),
            Answer("after interrupt"));
        var repository = new EventRepository(_database);
        var safe = new GatedTool("safe", parallelSafe: true);
        var unsafeTool = new GatedTool("unsafe", parallelSafe: false);
        await using var session = Session(
            provider,
            repository,
            [new TestTool(safe), new TestTool(unsafeTool)],
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await safe.Started("safe-1", cancellationToken);
        await safe.Started("safe-2", cancellationToken);
        await session.Interrupt(cancellationToken);

        _ = await Assert.That(session.Activity.Capture().CurrentTool).IsNull();
        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:safe-1:safe | started:safe-2:safe | cancelled:safe-1:safe | "
            + "cancelled:safe-2:safe | cancelled:unsafe:unsafe | cancelled:unstarted:safe");

        _ = await session.Send([ConversationPart.TextPart("next prompt")], "msg-2", Delivery.Steer, new IncomingActivity(IncomingActivityKind.Input, string.Empty), cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join(" | ", provider.Requests[1].Messages
            .Where(message => message.Role == LLMRole.Tool)
            .Select(message => message.ToolCallId)))
            .IsEqualTo("safe-1 | safe-2 | unsafe | unstarted");
        provider.Release();
        await session.DisposeAsync();
        _ = await Assert.That(repository.ToolTerminals("agent")).Count().IsEqualTo(4);
        _ = await Assert.That(repository.ToolTerminals("agent").Count(terminal =>
            terminal.Status == ToolExecutionStatus.Cancelled)).IsEqualTo(4);
    }

    private static string BlobPath(string notice)
    {
        const string prefix = "Tool output exceeded 64 KiB and was saved to ";
        const string suffix = ".";
        return notice[prefix.Length..^suffix.Length];
    }

    private static LLMEvent Answer(string text, params LLMToolCall[] toolCalls) =>
        LLMEvent.Completed("stop", 1, 0, 1, text, toolCalls);

    // Joined rather than compared item by item: order is what these assert,
    // and one string says so without a structural comparison.
    private static string Prompts(LLMRequest request) =>
        string.Join(
            " | ",
            request.Messages.Where(message => message.Role == LLMRole.User).Select(message => message.Content));

    private static string Conversation(IEventRepository repository) =>
        string.Join(" | ", repository.Messages("agent"));

    private static string Endings(IEventRepository repository) =>
        string.Join(
            " | ",
            repository.Replay()
                .Where(published => published.PayloadCase == Event.PayloadOneofCase.TurnEnded)
                .Select(published => published.TurnEnded.FinishReason));

    private static int Payloads(IEventRepository repository, Event.PayloadOneofCase payload) =>
        repository.Replay().Count(published => published.PayloadCase == payload);

    private static string ToolLifecycle(IEventRepository repository) =>
        string.Join(
            " | ",
            repository.Replay().Select(published => published.PayloadCase switch
            {
                Event.PayloadOneofCase.ToolStarted =>
                    $"started:{published.ToolStarted.ToolCallId}:{published.ToolStarted.ToolName}",
                Event.PayloadOneofCase.ToolFinished =>
                    $"finished:{published.ToolFinished.ToolCallId}:{published.ToolFinished.ToolName}",
                Event.PayloadOneofCase.ToolCancelled =>
                    $"cancelled:{published.ToolCancelled.ToolCallId}:{published.ToolCancelled.ToolName}",
                Event.PayloadOneofCase.ToolError =>
                    $"error:{published.ToolError.ToolCallId}:{published.ToolError.ToolName}:{published.ToolError.Message}",
                _ => null,
            }).Where(value => value is not null));

    private static string SelectionSummary(AgentTurnSelection selection) =>
        $"{selection.Profile?.Id}:{selection.SecurityProfile.ReadOnly}";

    private IAgentSession Session(
        ILLMProvider provider,
        IEventRepository repository,
        IReadOnlyList<TestTool> toolFactories,
        CancellationToken lifetime) =>
        Session(provider, repository, toolFactories, 0, 0, 0, 0, lifetime);

    private IAgentSession Session(
        ILLMProvider provider,
        IEventRepository repository,
        IReadOnlyList<TestTool> toolFactories,
        IMode profile,
        CancellationToken lifetime) =>
        Session(provider, repository, toolFactories, profile, 0, 0, 0, 0, lifetime);

    private IAgentSession Session(
        ILLMProvider provider,
        IEventRepository repository,
        IReadOnlyList<TestTool> toolFactories,
        ToolDefinitionCatalog definitions,
        CancellationToken lifetime) =>
        Session(provider, repository, toolFactories, definitions, profile: null, 0, 0, 0, 0, lifetime);

    private IAgentSession Session(
        ILLMProvider provider,
        IEventRepository repository,
        IReadOnlyList<TestTool> toolFactories,
        int contextWindow,
        double inputPrice,
        double cachedInputPrice,
        double outputPrice,
        CancellationToken lifetime) =>
        Session(
            provider,
            repository,
            toolFactories,
            new TestToolDefinitionsFixture([.. toolFactories.Select(factory => factory.Tool.Name)]).Definitions,
            profile: null,
            contextWindow,
            inputPrice,
            cachedInputPrice,
            outputPrice,
            lifetime);

    private IAgentSession Session(
        ILLMProvider provider,
        IEventRepository repository,
        IReadOnlyList<TestTool> toolFactories,
        IMode? profile,
        int contextWindow,
        double inputPrice,
        double cachedInputPrice,
        double outputPrice,
        CancellationToken lifetime) =>
        Session(
            provider,
            repository,
            toolFactories,
            new TestToolDefinitionsFixture([.. toolFactories.Select(factory => factory.Tool.Name)]).Definitions,
            profile,
            contextWindow,
            inputPrice,
            cachedInputPrice,
            outputPrice,
            lifetime);

    private IAgentSession SessionWithInputLimit(
        ILLMProvider provider,
        IEventRepository repository,
        int contextWindow,
        int maximumInputTokens,
        CancellationToken lifetime)
    {
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id)
        {
            ContextWindow = contextWindow,
            MaxInputTokens = maximumInputTokens,
        });
        var identity = AgentIdentity.Main("agent", string.Empty, TestModels.PromptTemplates);
        var dependencies = TestModels.Dependencies(identity, _broker, repository, lifetime);
        _dependencies.Add(dependencies);
        return new AgentSession(identity, AgentSessionParentScope.Root(), new ModelSelector(model.Selector), TestModels.Route(model), _broker, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(_blobDirectory), TestModels.CompactionGroupBlobs(), new Compactor(int.MaxValue, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null), new ContextCadence(), TestModels.PromptTemplates, dependencies.ChildQuestions, dependencies.ExitReminder, dependencies.Profile, new TestCompletionCallbacksFixture(dependencies.ChildQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, repository, _broker).Callbacks, new SecurityProfileTestFixture(SecurityProfile.Compose(readOnly: false, [], [], [])).Security, dependencies.Status, new AgentSessionActivity(TimeProvider.System), TestDiagnosticLog.Instance, lifetime);
    }

    private IAgentSession SessionWithSkills(
        ILLMProvider provider,
        IEventRepository repository,
        IReadOnlyList<TestTool> toolFactories,
        IMode profile,
        AgentSkills skills,
        SecurityProfile? securityProfile,
        CancellationToken lifetime)
    {
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var identity = AgentIdentity.Main("agent", string.Empty, TestModels.PromptTemplates);
        var dependencies = TestModels.Dependencies(identity, _broker, repository, lifetime);
        _dependencies.Add(dependencies);
        var security = new AgentSessionSecurity(
            securityProfile ?? SecurityProfile.Compose(readOnly: false, [], [], []),
            ProjectWorkspace.FromLaunchDirectory(Directory.GetCurrentDirectory()),
            Directory.GetCurrentDirectory());
        var prompt = new CompositeSystemPromptProvider(
            "test:skill-system-prompt",
            [
                new ConfiguredSystemPromptProvider("runtime:test-base", "base"),
                new AgentSkillPromptProvider(skills),
            ]).Materialize(identity);
        return new AgentSession(
            identity,
            AgentSessionParentScope.Root(),
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            _broker,
            repository,
            repository.GetRuntimeStatistics(),
            [.. toolFactories.Select(tool => tool.Factory)],
            new TestToolDefinitionsFixture([.. toolFactories.Select(factory => factory.Tool.Name)]).Definitions,
            prompt,
            new ToolOutputBlobStore(_blobDirectory),
            TestModels.CompactionGroupBlobs(),
            new Compactor(int.MaxValue, 30, 60_000, 1024, TestModels.PromptTemplates),
            new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null),
            new ContextCadence(),
            TestModels.PromptTemplates,
            dependencies.ChildQuestions,
            dependencies.ExitReminder,
            profile,
            new TestCompletionCallbacksFixture(dependencies.ChildQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, repository, _broker).Callbacks,
            security,
            skills,
            new RequestLimitsConfig(),
            dependencies.Status,
            new AgentSessionActivity(TimeProvider.System),
            TestDiagnosticLog.Instance,
            lifetime);
    }

    private IAgentSession SessionWithImageLimit(
        ILLMProvider provider,
        IEventRepository repository,
        IReadOnlyList<TestTool> toolFactories,
        int imageByteLimit,
        CancellationToken lifetime)
    {
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var identity = AgentIdentity.Main("agent", string.Empty, TestModels.PromptTemplates);
        var dependencies = TestModels.Dependencies(identity, _broker, repository, lifetime);
        _dependencies.Add(dependencies);
        var skills = new AgentSkills(new SkillCatalog([], () => (SkillConfiguration.Default, 0L)), TestModels.PromptTemplates);
        var security = new AgentSessionSecurity(
            SecurityProfile.Compose(readOnly: false, [], [], []),
            ProjectWorkspace.FromLaunchDirectory(Directory.GetCurrentDirectory()),
            Directory.GetCurrentDirectory());
        return new AgentSession(
            identity,
            AgentSessionParentScope.Root(),
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            _broker,
            repository,
            repository.GetRuntimeStatistics(),
            [.. toolFactories.Select(tool => tool.Factory)],
            new TestToolDefinitionsFixture([.. toolFactories.Select(factory => factory.Tool.Name)]).Definitions,
            TestModels.MaterializePrompt(identity, ".", "."),
            new ToolOutputBlobStore(_blobDirectory),
            TestModels.CompactionGroupBlobs(),
            new Compactor(int.MaxValue, 30, 60_000, 1024, TestModels.PromptTemplates),
            new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null),
            new ContextCadence(),
            TestModels.PromptTemplates,
            dependencies.ChildQuestions,
            dependencies.ExitReminder,
            dependencies.Profile,
            new TestCompletionCallbacksFixture(dependencies.ChildQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, repository, _broker).Callbacks,
            security,
            skills,
            new RequestLimitsConfig { ImageBytesPerToolCycle = imageByteLimit },
            dependencies.Status,
            new AgentSessionActivity(TimeProvider.System),
            TestDiagnosticLog.Instance,
            lifetime);
    }

    private IAgentSession SessionWithCompletionCallbacks(
        ILLMProvider provider,
        IEventRepository repository,
        IReadOnlyList<IAgentTurnCompletionCallback> completionCallbacks,
        IMode profile,
        CancellationToken lifetime)
    {
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var identity = AgentIdentity.Main("agent", string.Empty, TestModels.PromptTemplates);
        var dependencies = TestModels.Dependencies(identity, _broker, repository, lifetime);
        _dependencies.Add(dependencies);
        return new AgentSession(identity, AgentSessionParentScope.Root(), new ModelSelector(model.Selector), TestModels.Route(model), _broker, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(_blobDirectory), TestModels.CompactionGroupBlobs(), new Compactor(int.MaxValue, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null), new ContextCadence(), TestModels.PromptTemplates, dependencies.ChildQuestions, dependencies.ExitReminder, profile, completionCallbacks, new SecurityProfileTestFixture(SecurityProfile.Compose(readOnly: false, [], [], [])).Security, dependencies.Status, new AgentSessionActivity(TimeProvider.System), TestDiagnosticLog.Instance, lifetime);
    }

    private IAgentSession Session(
        ILLMProvider provider,
        IEventRepository repository,
        IReadOnlyList<TestTool> toolFactories,
        ToolDefinitionCatalog definitions,
        IMode? profile,
        int contextWindow,
        double inputPrice,
        double cachedInputPrice,
        double outputPrice,
        CancellationToken lifetime)
    {
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id)
        {
            ContextWindow = contextWindow,
            InputPrice = inputPrice,
            CachedInputPrice = cachedInputPrice,
            OutputPrice = outputPrice,
            Fields = ModelMetadataFields.InputPrice
                | ModelMetadataFields.OutputPrice
                | (cachedInputPrice > 0 ? ModelMetadataFields.CachedInputPrice : ModelMetadataFields.None),
        });
        var identity = AgentIdentity.Main("agent", string.Empty, TestModels.PromptTemplates);
        using var dependencies = TestModels.Dependencies(identity, _broker, repository, lifetime);
        return new AgentSession(identity, AgentSessionParentScope.Root(), new ModelSelector(model.Selector), TestModels.Route(model), _broker, repository, [.. toolFactories.Select(tool => tool.Factory)], definitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(_blobDirectory), TestModels.CompactionGroupBlobs(), new Compactor(int.MaxValue, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null), new ContextCadence(), TestModels.PromptTemplates, dependencies.ChildQuestions, dependencies.ExitReminder, profile ?? dependencies.Profile, new TestCompletionCallbacksFixture(dependencies.ChildQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, repository, _broker).Callbacks, new SecurityProfileTestFixture(SecurityProfile.Compose(readOnly: false, [], [], [])).Security, dependencies.Status, new AgentSessionActivity(TimeProvider.System), TestDiagnosticLog.Instance, lifetime);
    }

    private sealed class DrainProfile
    {
        public DrainProfile(int maxTurns)
            : this("test", maxTurns, null, new HashSet<string>(StringComparer.Ordinal), readOnly: false)
        {
        }

        public DrainProfile(
            int maxTurns,
            IReadOnlyList<string>? allowedTools,
            IReadOnlySet<string> disabledTools)
            : this("test", maxTurns, allowedTools, disabledTools, readOnly: false)
        {
        }

        public DrainProfile(
            string id,
            int maxTurns,
            IReadOnlyList<string>? allowedTools,
            IReadOnlySet<string> disabledTools,
            bool readOnly)
        {
            IAgentProfile profile = new AgentProfile(
                id,
                new ProfileConfig("Test prompt", "Test profile.", allowedTools, maxTurns, 3, readOnly, true, false, true, []),
                [],
                [],
                disabledTools);
            Mode = new NoopMode(profile, profile.SecurityProfile);
        }

        public IMode Mode { get; }
    }

    private sealed class GatedCompletionCallback : IAgentTurnCompletionCallback
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocations;

        public async ValueTask<AgentTurnCompletionOutcome> Complete(
            AgentTurnCompletionCandidate candidate,
            CancellationToken cancellationToken)
        {
            _ = candidate;
            if (Interlocked.Increment(ref _invocations) == 1)
            {
                _ = _entered.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return AgentTurnCompletionOutcome.Retry(
                    "retry first turn",
                    false,
                    false,
                    false,
                    null);
            }

            return AgentTurnCompletionOutcome.Continue(null, null);
        }

        internal Task WaitUntilEntered(CancellationToken cancellationToken) =>
            _entered.Task.WaitAsync(cancellationToken);

        internal void Release() => _ = _released.TrySetResult();
    }

    private sealed class RecordingCompletionCallback(
        Func<AgentTurnCompletionCandidate, int, AgentTurnCompletionOutcome> complete)
        : IAgentTurnCompletionCallback
    {
        private int _invocations;

        public ValueTask<AgentTurnCompletionOutcome> Complete(
            AgentTurnCompletionCandidate candidate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(complete(candidate, ++_invocations));
        }
    }

    private sealed class SurvivingTool(string name) : ITool
    {
        public string Name => name;

        public bool IsEnabledAfterInterruption => true;

        public Task<ToolExecutionResult> Execute(
            ToolInvocation invocation,
            AgentTurnSelection selection,
            CancellationToken cancellationToken) =>
            Task.FromResult<ToolExecutionResult>("survived");
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class GatedTool(string name, bool parallelSafe) : ITool
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _released = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _completed = new(StringComparer.Ordinal);
        private int _active;
        private int _maximumActive;

        public string Name => name;

        public int MaximumActive => Volatile.Read(ref _maximumActive);

        public bool IsParallelSafe(ToolInvocation invocation)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            return parallelSafe;
        }

        public Task Started(string callId, CancellationToken cancellationToken) =>
            _started.GetOrAdd(callId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(cancellationToken);

        public bool HasStarted(string callId) => _started.TryGetValue(callId, out var started) && started.Task.IsCompleted;

        public void Release(string callId) =>
            _ = _released.GetOrAdd(callId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

        public async Task Finished(string callId, CancellationToken cancellationToken) =>
            await _completed.GetOrAdd(callId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(cancellationToken);

        public async Task<ToolExecutionResult> Execute(
            ToolInvocation invocation,
            AgentTurnSelection selection,
            CancellationToken cancellationToken)
        {
            var started = _started.GetOrAdd(invocation.CallId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            var released = _released.GetOrAdd(invocation.CallId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _ = started.TrySetResult();
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                await released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return invocation.CallId;
            }
            finally
            {
                _ = Interlocked.Decrement(ref _active);
                _ = _completed.GetOrAdd(invocation.CallId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            }
        }

        private void UpdateMaximum(int active)
        {
            var observed = Volatile.Read(ref _maximumActive);
            while (active > observed)
            {
                var previous = Interlocked.CompareExchange(ref _maximumActive, active, observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }
    }

    private sealed record TestTool(ITool Tool, IToolFactory Factory)
    {
        public TestTool(ITool tool)
            : this(tool, new FixedToolFactory(tool))
        {
        }
    }

    private sealed class CountingToolFactory(string name) : IToolFactory
    {
        public int CreateCount { get; private set; }

        public ITool Tool { get; } = new NamedTool(name);

        public ITool Create(IAgentSession session)
        {
            CreateCount++;
            return Tool;
        }
    }

    private sealed class DisposalBlockedProvider : ILLMProvider
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "disposal-blocked";

        public IReadOnlyList<LLMModel> SeedModels() => [];

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LLMModel>>([]);

        public async IAsyncEnumerable<LLMEvent> Call(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = _entered.TrySetResult();
            await _released.Task.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield return Answer("done");
        }

        internal Task WaitUntilEntered(CancellationToken cancellationToken) =>
            _entered.Task.WaitAsync(cancellationToken);

        internal void Release() => _ = _released.TrySetResult();
    }

    private sealed class RequestPhaseProvider(bool fail, IReadOnlyList<LLMEvent> events) : ILLMProvider, IDisposable
    {
        private readonly SemaphoreSlim _arrived = new(0);
        private readonly SemaphoreSlim _released = new(0);

        public string Id => "request-phase";

        public IReadOnlyList<LLMModel> SeedModels() => [];

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LLMModel>>([]);

        public async IAsyncEnumerable<LLMEvent> Call(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var next in events)
            {
                yield return next;
                _ = _arrived.Release();
                await _released.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (fail)
            {
                throw new ProviderHttpException(400, "invalid_request", "bad", "broken", "provider body");
            }

            yield return Answer("done");
        }

        public Task Arrived(CancellationToken cancellationToken) => _arrived.WaitAsync(cancellationToken);

        public void Release() => _released.Release();

        public void Dispose()
        {
            _arrived.Dispose();
            _released.Dispose();
        }
    }

    private sealed class FailingProvider(Exception failure) : ILLMProvider
    {
        public string Id => "failing";

        public IReadOnlyList<LLMModel> SeedModels() => [];

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LLMModel>>([]);

        public async IAsyncEnumerable<LLMEvent> Call(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Model.Length > 0)
            {
                throw failure;
            }

            yield return LLMEvent.Completed("stop", 0, 0, 0, string.Empty, []);
        }
    }

    private sealed class NamedTool(string name) : ITool
    {
        public string Name => name;

        public Task<ToolExecutionResult> Execute(
            ToolInvocation invocation,
            AgentTurnSelection selection,
            CancellationToken cancellationToken) =>
            Task.FromResult<ToolExecutionResult>(name);
    }

    private sealed class RecordingToolFactory : IToolFactory
    {
        public RecordingTool RecordingTool { get; } = new();

        public ITool Tool => RecordingTool;

        public int CreateCount { get; private set; }

        public ITool Create(IAgentSession session)
        {
            CreateCount++;
            return Tool;
        }
    }

    private sealed class RecordingTool : ITool
    {
        private readonly List<AgentTurnSelection> _selections = [];

        public string Name => "record";

        public IReadOnlyList<AgentTurnSelection> Selections => _selections;

        public Task<ToolExecutionResult> Execute(
            ToolInvocation invocation,
            AgentTurnSelection selection,
            CancellationToken cancellationToken)
        {
            _selections.Add(selection);
            return Task.FromResult<ToolExecutionResult>("recorded");
        }
    }
}
