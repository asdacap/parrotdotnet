using System.Collections.Concurrent;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Llm.Wire;
using Parrot.Protocol;
using Parrot.Security;
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
    private readonly EventBroker _broker = new();
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
        var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);

        // Admitted while the first turn is parked inside the provider. The old
        // code started a second Run here, on the same history.
        _ = await session.Send([ConversationPart.TextPart("second prompt")], "msg-2", Delivery.Steer, cancellationToken);

        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();

        await session.Settled();

        // Two calls, not four: one drain answered both.
        _ = await Assert.That(provider.Requests.Count).IsEqualTo(2);
        _ = await Assert.That(Prompts(provider.Requests[1])).IsEqualTo("first prompt | second prompt");
        _ = await Assert.That(Conversation(repository)).IsEqualTo(
            "user: first prompt | assistant: first answer | user: second prompt | assistant: second answer");
    }

    [Test]
    public async Task A_queued_prompt_takes_a_turn_of_its_own_once_the_first_one_stops(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(Answer("first answer"), Answer("second answer"));
        var repository = new EventRepository(_database);
        var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await session.Send([ConversationPart.TextPart("queued prompt")], "msg-2", Delivery.Queue, cancellationToken);

        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();

        await session.Settled();

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
                    null,
                    false)
                : AgentTurnCompletionOutcome.Continue(null, null);
        });
        var third = new RecordingCompletionCallback((candidate, _) =>
        {
            calls.Add($"third:{candidate.MessageId}");
            return AgentTurnCompletionOutcome.Continue(null, null);
        });
        var session = SessionWithCompletionCallbacks(provider, repository, [first, second, third], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "message", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();

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
    public async Task A_drain_continues_until_every_queued_prompt_is_answered(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer("first answer"), Answer("second answer"), Answer("third answer"));
        var repository = new EventRepository(_database);
        var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await session.Send([ConversationPart.TextPart("second prompt")], "msg-2", Delivery.Queue, cancellationToken);
        _ = await session.Send([ConversationPart.TextPart("third prompt")], "msg-3", Delivery.Queue, cancellationToken);

        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();

        _ = await Assert.That(provider.Requests.Count).IsEqualTo(3);
        _ = await Assert.That(repository.HasPendingInputs("agent")).IsFalse();
        _ = await Assert.That(Conversation(repository)).IsEqualTo(
            "user: first prompt | assistant: first answer | user: second prompt | assistant: second answer | "
            + "user: third prompt | assistant: third answer");
    }

    [Test]
    [Skip("Existing drain regression at cbfc8dd: terminal assistant output is not retained after a steered final provider request.")]
    public async Task A_steer_admitted_during_a_tool_round_joins_the_turn_already_running(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "settled", "{}")), Answer("done"));
        var repository = new EventRepository(_database);
        var session = Session(
            provider,
            repository,
            [new FixedToolFactory(new SettledTool("settled"))],
            Profile(maxTurns: 2),
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await session.Send([ConversationPart.TextPart("steer")], "msg-2", Delivery.Steer, cancellationToken);

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

        // One turn, not two: the steer was answered inside the turn that was
        // already running rather than starting one of its own.
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.TurnStarted)).IsEqualTo(1);
        _ = await Assert.That(Conversation(repository))
            .IsEqualTo("user: first prompt | assistant:  | tool: settled | user: steer | "
                + "system: This is the final provider request allowed for the current turn. "
                + "Tools are unavailable for this request. Do not request or invoke tools. "
                + "Provide the best possible final answer using the information already available. | assistant: done");
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
            var session = Session(provider, repository, [new FixedToolFactory(new SettledTool(result))], cancellationToken);

            _ = await session.Send([ConversationPart.TextPart("prompt")], $"msg-{result.Length}", Delivery.Steer, cancellationToken);
            await provider.Arrived(cancellationToken);
            provider.Release();
            await provider.Arrived(cancellationToken);
            provider.Release();
            await session.Settled();

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
    public async Task Statistics_include_tool_rounds_are_durable_and_restored(
        CancellationToken cancellationToken)
    {
        var repository = new EventRepository(_database);
        using (var firstProvider = new SteppedProvider(
            LLMEvent.Completed(
                "tool_calls", 10, 3, 4, string.Empty, [new LLMToolCall("call-1", "settled", "{}")]),
            LLMEvent.Completed("stop", 7, 2, 5, "first", [])))
        {
            var firstSession = Session(
                firstProvider,
                repository,
                [new FixedToolFactory(new SettledTool("settled"))],
                128,
                0.125,
                0.025,
                0.25,
                cancellationToken);
            _ = await firstSession.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, cancellationToken);
            await firstProvider.Arrived(cancellationToken);
            firstProvider.Release();
            await firstProvider.Arrived(cancellationToken);
            firstProvider.Release();
            await firstSession.Settled();
        }

        using (var secondProvider = new SteppedProvider(
            LLMEvent.Completed("stop", 6, 1, 2, "second", [])))
        {
            var restoredSession = Session(secondProvider, repository, [], 128, 0.125, 0.025, 0.25, cancellationToken);
            _ = await restoredSession.Send([ConversationPart.TextPart("second prompt")], "msg-2", Delivery.Steer, cancellationToken);
            await secondProvider.Arrived(cancellationToken);
            secondProvider.Release();
            await restoredSession.Settled();
        }

        var replay = repository.Replay().ToList();
        var statistics = replay
            .Where(published => published.PayloadCase == Event.PayloadOneofCase.AgentStatisticsUpdated)
            .Select(published => published.AgentStatisticsUpdated)
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
            .IsEqualTo("10:3:4:10:128 | 17:5:9:7:128 | 23:6:11:6:128");
        _ = await Assert.That(statistics[0].InputCost).IsEqualTo(0.95);
        _ = await Assert.That(statistics[0].OutputCost).IsEqualTo(1.0);
        _ = await Assert.That(statistics[1].InputCost).IsEqualTo(1.625);
        _ = await Assert.That(statistics[1].OutputCost).IsEqualTo(2.25);
        _ = await Assert.That(statistics[2].InputCost).IsEqualTo(2.275);
        _ = await Assert.That(statistics[2].OutputCost).IsEqualTo(2.75);
        _ = await Assert.That(
            string.Join(" | ", endings.Select(ended => $"{ended.InputTokens}:{ended.OutputTokens}")))
            .IsEqualTo("17:9 | 23:11");
        _ = await Assert.That(replay.FindIndex(
            published => published.PayloadCase == Event.PayloadOneofCase.AgentStatisticsUpdated))
            .IsLessThan(replay.FindIndex(published => published.PayloadCase == Event.PayloadOneofCase.ToolStarted));
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
        var session = Session(
            provider,
            repository,
            [new FixedToolFactory(new SettledTool("settled"))],
            Profile(maxTurns: 2),
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();

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
                published.PayloadCase == Event.PayloadOneofCase.AgentStatisticsUpdated);
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
        var firstProfile = Profile(
            "first-profile",
            3,
            ["first"],
            new HashSet<string>(StringComparer.Ordinal),
            readOnly: false);
        var secondProfile = Profile(
            "second-profile",
            3,
            ["second"],
            new HashSet<string>(StringComparer.Ordinal),
            readOnly: true);
        var session = Session(
            provider,
            repository,
            [firstFactory, secondFactory],
            firstProfile,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join(" | ", provider.Requests[0].Tools.Select(tool => tool.Name)))
            .IsEqualTo("first");
        provider.Release();
        await session.Settled();

        session.UpdateSelection(session.Selection().RequestedModel, secondProfile);
        _ = await session.Send([ConversationPart.TextPart("second prompt")], "msg-2", Delivery.Steer, cancellationToken);
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
        var writable = Profile(
            "writable",
            3,
            ["record"],
            new HashSet<string>(StringComparer.Ordinal),
            readOnly: false);
        var readOnly = Profile(
            "read-only",
            3,
            ["record"],
            new HashSet<string>(StringComparer.Ordinal),
            readOnly: true);
        var session = Session(provider, repository, [factory], writable, cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        session.UpdateSelection(session.Selection().RequestedModel, readOnly);
        provider.Release();
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join(" | ", factory.RecordingTool.Selections.Select(SelectionSummary)))
            .IsEqualTo("writable:True");
        provider.Release();
        await session.Settled();

        _ = await session.Send([ConversationPart.TextPart("second prompt")], "msg-2", Delivery.Steer, cancellationToken);
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
        var session = Session(
            provider,
            repository,
            [new FixedToolFactory(new SettledTool("settled")), new FixedToolFactory(new HeldTool())],
            Profile(
                3,
                ["settled", "held"],
                new HashSet<string>(["settled"], StringComparer.Ordinal)),
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join(" | ", provider.Requests[0].Tools.Select(tool => tool.Name)))
            .IsEqualTo("held");
        provider.Release();
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join(" | ", provider.Requests[1].Tools.Select(tool => tool.Name)))
            .IsEqualTo("held");
        provider.Release();
        await session.Settled();

        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:call-disabled:settled | error:call-disabled:settled:unknown tool settled");
    }

    [Test]
    [Skip("Existing drain regression at cbfc8dd: queued input is not promoted after the final provider request.")]
    public async Task A_profile_turn_omits_tools_on_its_final_provider_request_and_resets_for_queued_input(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "settled", "{}")),
            Answer("first answer"),
            Answer("queued answer"));
        var repository = new EventRepository(_database);
        var session = Session(
            provider,
            repository,
            [new FixedToolFactory(new SettledTool("settled"))],
            Profile(maxTurns: 2),
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await session.Send([ConversationPart.TextPart("queued prompt")], "msg-2", Delivery.Queue, cancellationToken);
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

        _ = await Assert.That(Endings(repository)).IsEqualTo("stop | stop");
        _ = await Assert.That(Conversation(repository)).IsEqualTo(
            "user: first prompt | assistant:  | tool: settled | "
            + "system: This is the final provider request allowed for the current turn. "
            + "Tools are unavailable for this request. Do not request or invoke tools. "
            + "Provide the best possible final answer using the information already available. | "
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
            var firstSession = Session(firstProvider, repository, [], Profile(maxTurns: 1), cancellationToken);

            _ = await firstSession.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, cancellationToken);
            await firstProvider.Arrived(cancellationToken);
            firstProvider.Release();
            await firstSession.Settled();
        }

        using var restoredProvider = new SteppedProvider(Answer("second answer"), Answer("third answer"));
        var restoredSession = Session(
            restoredProvider,
            repository,
            [new FixedToolFactory(new SettledTool("settled"))],
            Profile(maxTurns: 2),
            cancellationToken);

        _ = await restoredSession.Send([ConversationPart.TextPart("second prompt")], "msg-2", Delivery.Steer, cancellationToken);
        await restoredProvider.Arrived(cancellationToken);
        _ = await Assert.That(restoredProvider.Requests[0].Tools).HasSingleItem();
        _ = await Assert.That(restoredProvider.Requests[0].Messages.Count(message =>
            message.Role == LLMRole.System
            && message.Content.Contains("Tool access is restored", StringComparison.Ordinal))).IsEqualTo(1);
        restoredProvider.Release();
        await restoredSession.Settled();

        _ = await restoredSession.Send([ConversationPart.TextPart("third prompt")], "msg-3", Delivery.Steer, cancellationToken);
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
    public async Task A_tool_call_returned_after_tools_are_omitted_is_settled_and_fails_the_turn(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "settled", "{}")));
        var repository = new EventRepository(_database);
        var session = Session(
            provider,
            repository,
            [new FixedToolFactory(new SettledTool("settled"))],
            Profile(maxTurns: 1),
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests.Single().Tools).IsEmpty();
        provider.Release();
        await session.Settled();

        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:call-1:settled | error:call-1:settled:unknown tool settled");
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
        var session = Session(
            provider,
            repository,
            [new FixedToolFactory(new SettledTool("settled"))],
            TestModels.EmptyToolDefinitions,
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await session.Settled();

        _ = await Assert.That(repository.Replay().Last(published =>
            published.PayloadCase == Event.PayloadOneofCase.TurnFailed).TurnFailed.Message)
            .Contains("tools.settled is not defined");
        _ = await Assert.That(provider.Requests).IsEmpty();
    }

    [Test]
    public async Task Terminal_provider_failures_publish_the_response_body(CancellationToken cancellationToken)
    {
        var provider = new FailingProvider();
        var repository = new EventRepository(_database);
        var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await session.Settled();

        var failure = repository.Replay().Last(published =>
            published.PayloadCase == Event.PayloadOneofCase.TurnFailed).TurnFailed;
        _ = await Assert.That(failure.Message).Contains("HTTP 400");
        _ = await Assert.That(failure.ProviderResponseBody).IsEqualTo("provider body");
    }

    [Test]
    public async Task An_unknown_tool_emits_an_error_before_the_turn_continues(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "missing", "{}")), Answer("done"));
        var repository = new EventRepository(_database);
        var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();

        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:call-1:missing | error:call-1:missing:unknown tool missing");
    }

    [Test]
    public async Task A_settled_tool_is_cleared_before_the_next_provider_request(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer("tool preface", new LLMToolCall("call-1", "settled", "{}")), Answer("done"));
        var repository = new EventRepository(_database);
        var session = Session(
            provider,
            repository,
            [new FixedToolFactory(new SettledTool("result"))],
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);

        var executing = session.Activity.Capture();
        _ = await Assert.That(executing.CurrentTool).IsNull();
        _ = await Assert.That(executing.Recent).Count().IsEqualTo(1);
        _ = await Assert.That(executing.Recent[0].Content).IsEqualTo("tool preface");
        provider.Release();
        await session.Settled();
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
        var session = Session(
            provider,
            repository,
            [new FixedToolFactory(new FailureTool())],
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);

        _ = await Assert.That(session.Activity.Capture().CurrentTool).IsNull();
        provider.Release();
        await session.Settled();
        _ = await Assert.That(session.Activity.Capture().CurrentTool).IsNull();
    }

    [Test]
    public async Task An_unknown_tool_never_appears_as_current(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "missing", "{}")), Answer("done"));
        var repository = new EventRepository(_database);
        var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);

        _ = await Assert.That(session.Activity.Capture().CurrentTool).IsNull();
        provider.Release();
        await session.Settled();
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
        var session = Session(provider, repository, [new FixedToolFactory(heldTool)], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, cancellationToken);
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
        _ = await session.Send([ConversationPart.TextPart("second prompt")], "msg-2", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();

        var answered = string.Join(" | ", provider.Requests[1].Messages
            .Where(message => message.Role == LLMRole.Tool)
            .Select(message => message.ToolCallId));

        _ = await Assert.That(answered).IsEqualTo("call-1 | call-2");
        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:call-1:held | cancelled:call-1:held | cancelled:call-2:held");
    }

    [Test]
    public async Task An_interrupt_resumes_the_drain_for_input_that_was_still_pending(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(Answer("first answer"), Answer("queued answer"));
        var repository = new EventRepository(_database);
        var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("first prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);

        // Admitted but never promoted: the turn it would have joined is the
        // one being stopped.
        _ = await session.Send([ConversationPart.TextPart("queued prompt")], "msg-2", Delivery.Queue, cancellationToken);

        await session.Interrupt(cancellationToken);

        // Nothing was admitted after the interrupt, so a second provider call
        // can only be the drain resuming for what was left pending.
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();

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
        var session = Session(provider, repository, [new FixedToolFactory(tool)], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, cancellationToken);
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
        await session.Settled();

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
        var session = Session(
            provider,
            repository,
            [new FixedToolFactory(safe), new FixedToolFactory(unsafeTool)],
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, cancellationToken);
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
        await session.Settled();

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
        var session = Session(provider, repository, [new FixedToolFactory(tool)], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, cancellationToken);
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
        await session.Settled();

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
        var session = Session(provider, repository, [new FixedToolFactory(tool)], cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, cancellationToken);
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
        await session.Settled();
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
        var session = Session(
            provider,
            repository,
            [new FixedToolFactory(safe), new FixedToolFactory(unsafeTool)],
            cancellationToken);

        _ = await session.Send([ConversationPart.TextPart("prompt")], "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await safe.Started("safe-1", cancellationToken);
        await safe.Started("safe-2", cancellationToken);
        await session.Interrupt(cancellationToken);

        _ = await Assert.That(session.Activity.Capture().CurrentTool).IsNull();
        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:safe-1:safe | started:safe-2:safe | cancelled:safe-1:safe | "
            + "cancelled:safe-2:safe | cancelled:unsafe:unsafe | cancelled:unstarted:safe");

        _ = await session.Send([ConversationPart.TextPart("next prompt")], "msg-2", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join(" | ", provider.Requests[1].Messages
            .Where(message => message.Role == LLMRole.Tool)
            .Select(message => message.ToolCallId)))
            .IsEqualTo("safe-1 | safe-2 | unsafe | unstarted");
        provider.Release();
        await session.Settled();
        _ = await Assert.That(repository.ToolTerminals("agent")).Count().IsEqualTo(4);
        _ = await Assert.That(repository.ToolTerminals("agent").Count(terminal =>
            terminal.Status == ToolExecutionStatus.Cancelled)).IsEqualTo(4);
    }

    private static ToolDefinitionCatalog Document(IReadOnlyList<IToolFactory> factories) =>
        TestModels.DocumentTools([.. factories.Select(factory => ((ITestToolFactory)factory).Tool.Name)]);

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

    private static string Conversation(EventRepository repository) =>
        string.Join(" | ", repository.Messages("agent"));

    private static string Endings(EventRepository repository) =>
        string.Join(
            " | ",
            repository.Replay()
                .Where(published => published.PayloadCase == Event.PayloadOneofCase.TurnEnded)
                .Select(published => published.TurnEnded.FinishReason));

    private static int Payloads(EventRepository repository, Event.PayloadOneofCase payload) =>
        repository.Replay().Count(published => published.PayloadCase == payload);

    private static string ToolLifecycle(EventRepository repository) =>
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

    private static NoopMode Profile(int maxTurns) =>
        Profile(
            "test",
            maxTurns,
            null,
            new HashSet<string>(StringComparer.Ordinal),
            readOnly: false);

    private static NoopMode Profile(
        int maxTurns,
        IReadOnlyList<string>? allowedTools,
        IReadOnlySet<string> disabledTools) =>
        Profile("test", maxTurns, allowedTools, disabledTools, readOnly: false);

    private static NoopMode Profile(
        string id,
        int maxTurns,
        IReadOnlyList<string>? allowedTools,
        IReadOnlySet<string> disabledTools,
        bool readOnly)
    {
        var profile = new AgentProfile(
            id,
            new ProfileConfig("Test prompt", "Test profile.", allowedTools, maxTurns, 3, readOnly, true, false, true, []),
            [],
            [],
            disabledTools);
        return new NoopMode(profile, profile.SecurityProfile);
    }

    private static string SelectionSummary(AgentTurnSelection selection) =>
        $"{selection.Profile?.Id}:{selection.SecurityProfile.ReadOnly}";

    private AgentSession Session(
        ILLMProvider provider,
        EventRepository repository,
        IReadOnlyList<IToolFactory> toolFactories,
        CancellationToken lifetime) =>
        Session(provider, repository, toolFactories, 0, 0, 0, 0, lifetime);

    private AgentSession Session(
        ILLMProvider provider,
        EventRepository repository,
        IReadOnlyList<IToolFactory> toolFactories,
        IMode profile,
        CancellationToken lifetime) =>
        Session(provider, repository, toolFactories, profile, 0, 0, 0, 0, lifetime);

    private AgentSession Session(
        ILLMProvider provider,
        EventRepository repository,
        IReadOnlyList<IToolFactory> toolFactories,
        ToolDefinitionCatalog definitions,
        CancellationToken lifetime) =>
        Session(provider, repository, toolFactories, definitions, profile: null, 0, 0, 0, 0, lifetime);

    private AgentSession Session(
        ILLMProvider provider,
        EventRepository repository,
        IReadOnlyList<IToolFactory> toolFactories,
        int contextWindow,
        double inputPrice,
        double cachedInputPrice,
        double outputPrice,
        CancellationToken lifetime) =>
        Session(
            provider,
            repository,
            toolFactories,
            Document(toolFactories),
            profile: null,
            contextWindow,
            inputPrice,
            cachedInputPrice,
            outputPrice,
            lifetime);

    private AgentSession Session(
        ILLMProvider provider,
        EventRepository repository,
        IReadOnlyList<IToolFactory> toolFactories,
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
            Document(toolFactories),
            profile,
            contextWindow,
            inputPrice,
            cachedInputPrice,
            outputPrice,
            lifetime);

    private AgentSession SessionWithCompletionCallbacks(
        SteppedProvider provider,
        EventRepository repository,
        IReadOnlyList<IAgentTurnCompletionCallback> completionCallbacks,
        CancellationToken lifetime)
    {
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var identity = AgentIdentity.Main("agent", string.Empty, TestModels.PromptTemplates);
        var dependencies = TestModels.Dependencies(identity, _broker, repository, lifetime);
        _dependencies.Add(dependencies);
        return new AgentSession(identity, AgentSessionParentScope.Root(), new ModelSelector(model.Selector), TestModels.Route(model), _broker, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(_blobDirectory), new Compactor(int.MaxValue, 30, 60_000, 1024, TestModels.PromptTemplates), TestModels.PromptTemplates, dependencies.ChildQuestions, dependencies.ExitReminder, dependencies.Profile, completionCallbacks, SecurityProfileTestFactory.Create(SecurityProfile.Compose(readOnly: false, [], [], [])), dependencies.Status, dependencies.ChildRegistry, dependencies.Queues, new AgentSessionActivity(TimeProvider.System), lifetime);
    }

    private AgentSession Session(
        ILLMProvider provider,
        EventRepository repository,
        IReadOnlyList<IToolFactory> toolFactories,
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
        return new AgentSession(identity, AgentSessionParentScope.Root(), new ModelSelector(model.Selector), TestModels.Route(model), _broker, repository, toolFactories, definitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(_blobDirectory), new Compactor(int.MaxValue, 30, 60_000, 1024, TestModels.PromptTemplates), TestModels.PromptTemplates, dependencies.ChildQuestions, dependencies.ExitReminder, profile ?? dependencies.Profile, TestModels.CompletionCallbacks(dependencies.ChildQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, repository, _broker), SecurityProfileTestFactory.Create(SecurityProfile.Compose(readOnly: false, [], [], [])), dependencies.Status, dependencies.ChildRegistry, dependencies.Queues, new AgentSessionActivity(TimeProvider.System), lifetime);
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
            _started.GetOrAdd(callId, _ => NewCompletion()).Task.WaitAsync(cancellationToken);

        public bool HasStarted(string callId) => _started.TryGetValue(callId, out var started) && started.Task.IsCompleted;

        public void Release(string callId) =>
            _ = _released.GetOrAdd(callId, _ => NewCompletion()).TrySetResult();

        public async Task Finished(string callId, CancellationToken cancellationToken) =>
            await _completed.GetOrAdd(callId, _ => NewCompletion()).Task.WaitAsync(cancellationToken);

        public async Task<ToolExecutionResult> Execute(
            ToolInvocation invocation,
            AgentTurnSelection selection,
            CancellationToken cancellationToken)
        {
            var started = _started.GetOrAdd(invocation.CallId, _ => NewCompletion());
            var released = _released.GetOrAdd(invocation.CallId, _ => NewCompletion());
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
                _ = _completed.GetOrAdd(invocation.CallId, _ => NewCompletion()).TrySetResult();
            }
        }

        private static TaskCompletionSource NewCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private sealed class CountingToolFactory(string name) : IToolFactory, ITestToolFactory
    {
        private readonly NamedTool _tool = new(name);

        public int CreateCount { get; private set; }

        public ITool Tool => _tool;

        public ITool Create(AgentSession session)
        {
            CreateCount++;
            return _tool;
        }
    }

    private sealed class FailingProvider : ILLMProvider
    {
        public string Id => "failing";

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
                throw new ProviderHttpException(400, "invalid_request", "bad", "broken", "provider body");
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

    private sealed class RecordingToolFactory : IToolFactory, ITestToolFactory
    {
        public RecordingTool RecordingTool { get; } = new();

        public ITool Tool => RecordingTool;

        public int CreateCount { get; private set; }

        public ITool Create(AgentSession session)
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
