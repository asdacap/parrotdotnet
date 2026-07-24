using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

// The drain, driven through a provider that parks mid-call. Everything here
// depends on a turn still being in flight when the next prompt arrives, which
// is the whole of what a queue is for.
internal sealed class DrainTests : IDisposable
{
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly EventBroker _broker = new();

    public void Dispose()
    {
        _broker.Dispose();
        _database.Dispose();
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
    public async Task A_steer_admitted_during_a_tool_round_joins_the_turn_already_running(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            Answer(string.Empty, new LLMToolCall("call-1", "settled", "{}")), Answer("done"));
        var repository = new EventRepository(_database);
        var session = Session(provider, repository, [new FixedToolFactory(new SettledTool())], cancellationToken);

        _ = await session.Admit("first prompt", "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await session.Admit("steer", "msg-2", Delivery.Steer, cancellationToken);

        // The tool round settles and the turn carries on to its next boundary,
        // which is where the steer joins it.
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await session.Settled();

        // One turn, not two: the steer was answered inside the turn that was
        // already running rather than starting one of its own.
        _ = await Assert.That(Payloads(repository, Event.PayloadOneofCase.TurnStarted)).IsEqualTo(1);
        _ = await Assert.That(Conversation(repository))
            .IsEqualTo("user: first prompt | user: steer | assistant: done");
        _ = await Assert.That(Prompts(provider.Requests[1])).IsEqualTo("first prompt | steer");
        _ = await Assert.That(ToolLifecycle(repository)).IsEqualTo(
            "started:call-1:settled | finished:call-1:settled");
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
        var session = Session(provider, repository, [new FixedToolFactory(new HeldTool())], cancellationToken);

        _ = await session.Admit("first prompt", "msg-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();

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
            "cancelled:call-1:held | cancelled:call-2:held");
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

    private static LLMEvent Answer(string text, params LLMToolCall[] toolCalls) =>
        LLMEvent.Completed("stop", 1, 1, text, toolCalls);

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

    private AgentSession Session(
        ILLMProvider provider,
        EventRepository repository,
        IReadOnlyList<IToolFactory> toolFactories,
        CancellationToken lifetime) =>
        new(
            AgentIdentity.Main("agent"),
            provider,
            _broker,
            repository,
            toolFactories,
            new SystemContextBuilder(".", "2026-07-24"),
            new Compactor(120_000),
            lifetime)
        {
            Model = "model",
        };
}
