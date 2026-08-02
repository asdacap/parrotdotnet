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

    public void Dispose()
    {
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

        _ = await session.Admit("first prompt", "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);

        // Admitted while the first turn is parked inside the provider. The old
        // code started a second Run here, on the same history.
        _ = await session.Admit("second prompt", "msg-2", Delivery.Steer, cancellationToken);

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

        _ = await session.Admit("first prompt", "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await session.Admit("queued prompt", "msg-2", Delivery.Queue, cancellationToken);

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
    public async Task A_drain_continues_until_every_queued_prompt_is_answered(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer("first answer"), Answer("second answer"), Answer("third answer"));
        var repository = new EventRepository(_database);
        var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Admit("first prompt", "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await session.Admit("second prompt", "msg-2", Delivery.Queue, cancellationToken);
        _ = await session.Admit("third prompt", "msg-3", Delivery.Queue, cancellationToken);

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

        _ = await session.Admit("first prompt", "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await session.Admit("steer", "msg-2", Delivery.Steer, cancellationToken);

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

            _ = await session.Admit("prompt", $"msg-{result.Length}", Delivery.Steer, cancellationToken);
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
            _ = await firstSession.Admit("first prompt", "msg-1", Delivery.Steer, cancellationToken);
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
            _ = await restoredSession.Admit("second prompt", "msg-2", Delivery.Steer, cancellationToken);
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

        _ = await session.Admit("first prompt", "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join(" | ", provider.Requests[0].Tools.Select(tool => tool.Name)))
            .IsEqualTo("first");
        provider.Release();
        await session.Settled();

        session.UpdateSelection(session.Selection().RequestedModel, secondProfile);
        _ = await session.Admit("second prompt", "msg-2", Delivery.Steer, cancellationToken);
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

        _ = await session.Admit("first prompt", "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        session.UpdateSelection(session.Selection().RequestedModel, readOnly);
        provider.Release();
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join(" | ", factory.Tool.Selections.Select(SelectionSummary)))
            .IsEqualTo("writable:True");
        provider.Release();
        await session.Settled();

        _ = await session.Admit("second prompt", "msg-2", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join(" | ", factory.Tool.Selections.Select(SelectionSummary)))
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

        _ = await session.Admit("prompt", "msg-1", Delivery.Steer, cancellationToken);
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

        _ = await session.Admit("first prompt", "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await session.Admit("queued prompt", "msg-2", Delivery.Queue, cancellationToken);
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

            _ = await firstSession.Admit("first prompt", "msg-1", Delivery.Steer, cancellationToken);
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

        _ = await restoredSession.Admit("second prompt", "msg-2", Delivery.Steer, cancellationToken);
        await restoredProvider.Arrived(cancellationToken);
        _ = await Assert.That(restoredProvider.Requests[0].Tools).HasSingleItem();
        _ = await Assert.That(restoredProvider.Requests[0].Messages.Count(message =>
            message.Role == LLMRole.System
            && message.Content.Contains("Tool access is restored", StringComparison.Ordinal))).IsEqualTo(1);
        restoredProvider.Release();
        await restoredSession.Settled();

        _ = await restoredSession.Admit("third prompt", "msg-3", Delivery.Steer, cancellationToken);
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

        _ = await session.Admit("prompt", "msg-1", Delivery.Steer, cancellationToken);
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
    public async Task Terminal_provider_failures_publish_the_response_body(CancellationToken cancellationToken)
    {
        var provider = new FailingProvider();
        var repository = new EventRepository(_database);
        var session = Session(provider, repository, [], cancellationToken);

        _ = await session.Admit("prompt", "msg-1", Delivery.Steer, cancellationToken);
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

        _ = await session.Admit("prompt", "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();

        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:call-1:missing | error:call-1:missing:unknown tool missing");
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

        _ = await session.Admit("first prompt", "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await heldTool.Started.WaitAsync(cancellationToken);

        await session.Interrupt(cancellationToken);

        _ = await Assert.That(session.State).IsEqualTo(DrainState.Idle);
        _ = await Assert.That(Endings(repository)).Contains("interrupted");

        // The next prompt is what proves it: a provider rejects a history
        // holding a call with no result, so this call is the assertion.
        _ = await session.Admit("second prompt", "msg-2", Delivery.Steer, cancellationToken);
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

        _ = await session.Admit("first prompt", "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);

        // Admitted but never promoted: the turn it would have joined is the
        // one being stopped.
        _ = await session.Admit("queued prompt", "msg-2", Delivery.Queue, cancellationToken);

        await session.Interrupt(cancellationToken);

        // Nothing was admitted after the interrupt, so a second provider call
        // can only be the drain resuming for what was left pending.
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();

        _ = await Assert.That(repository.HasPendingInputs("agent")).IsFalse();
        _ = await Assert.That(Prompts(provider.Requests[1])).IsEqualTo("first prompt | queued prompt");
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

    private static AgentProfile Profile(int maxTurns) =>
        Profile(
            "test",
            maxTurns,
            null,
            new HashSet<string>(StringComparer.Ordinal),
            readOnly: false);

    private static AgentProfile Profile(
        int maxTurns,
        IReadOnlyList<string>? allowedTools,
        IReadOnlySet<string> disabledTools) =>
        Profile("test", maxTurns, allowedTools, disabledTools, readOnly: false);

    private static AgentProfile Profile(
        string id,
        int maxTurns,
        IReadOnlyList<string>? allowedTools,
        IReadOnlySet<string> disabledTools,
        bool readOnly) => new(
            id,
            new ProfileConfig("Test prompt", "Test profile.", allowedTools, maxTurns, 3, readOnly, true, []),
            [],
            [],
            disabledTools);

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
        IAgentProfile profile,
        CancellationToken lifetime) =>
        Session(provider, repository, toolFactories, profile, 0, 0, 0, 0, lifetime);

    private AgentSession Session(
        ILLMProvider provider,
        EventRepository repository,
        IReadOnlyList<IToolFactory> toolFactories,
        int contextWindow,
        double inputPrice,
        double cachedInputPrice,
        double outputPrice,
        CancellationToken lifetime) =>
        Session(provider, repository, toolFactories, profile: null, contextWindow, inputPrice, cachedInputPrice, outputPrice, lifetime);

    private AgentSession Session(
        ILLMProvider provider,
        EventRepository repository,
        IReadOnlyList<IToolFactory> toolFactories,
        IAgentProfile? profile,
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
        var identity = AgentIdentity.Main("agent", string.Empty);
        var dependencies = TestModels.Dependencies(identity, _broker, repository, lifetime);
        return new AgentSession(
            identity,
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            _broker,
            repository,
            toolFactories,
            TestModels.MaterializePrompt(identity, ".", "."),
            new TodoCollection("agent", repository, _broker),
            new ToolOutputBlobStore(_blobDirectory),
            new Compactor(int.MaxValue, 30, 60_000, 1024),
            dependencies.ActiveWorkReminder,
            profile ?? dependencies.Profile,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            dependencies.Status,
            dependencies.Registry,
            dependencies.Queues,
            lifetime);
    }

    private sealed class CountingToolFactory(string name) : IToolFactory
    {
        private readonly NamedTool _tool = new(name);

        public int CreateCount { get; private set; }

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

        public string Description => "Finishes at once.";

        public string ParametersJson => """{"type":"object","properties":{}}""";

        public Task<ToolExecutionResult> Execute(
            ToolInvocation invocation,
            AgentTurnSelection selection,
            CancellationToken cancellationToken) =>
            Task.FromResult<ToolExecutionResult>(name);
    }

    private sealed class RecordingToolFactory : IToolFactory
    {
        public RecordingTool Tool { get; } = new();

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

        public string Description => "Records the turn selection.";

        public string ParametersJson => """{"type":"object","properties":{}}""";

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
