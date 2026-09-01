using System.Collections.ObjectModel;
using Parrot.Agent;
using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class AgentSessionActivityTests
{
    [Test]
    public async Task Provider_activity_excludes_completed_and_raw_reasoning_content()
    {
        var time = new ControlledTimeProvider();
        var activity = new AgentSessionActivity(time);

        activity.ObserveProviderEvent(LLMEvent.Completed(string.Empty, 0, 0, 0, string.Empty, []));
        activity.ObserveProviderEvent(LLMEvent.ReasoningDelta(
            "private",
            LLMReasoningKind.Raw,
            string.Empty,
            completed: true));
        var empty = activity.Capture();

        activity.ObserveProviderEvent(LLMEvent.TextDelta("text"));
        time.Advance(TimeSpan.FromSeconds(1));
        activity.ObserveProviderEvent(LLMEvent.ReasoningDelta("reasoning"));
        time.Advance(TimeSpan.FromSeconds(2));
        activity.ObserveProviderEvent(LLMEvent.ToolCallDelta("call", "tool", "{}"));
        time.Advance(TimeSpan.FromSeconds(3));
        activity.ObserveProviderEvent(LLMEvent.Retry(1, TimeSpan.Zero, "retry"));
        time.Advance(TimeSpan.FromSeconds(4));
        activity.ObserveProviderEvent(LLMEvent.Completed(string.Empty, 0, 0, 0, string.Empty, []));
        var captured = activity.Capture();

        _ = await Assert.That(empty.LatestProviderActivityAge).IsEqualTo(TimeSpan.Zero);
        _ = await Assert.That(empty.Recent).IsEmpty();
        _ = await Assert.That(captured.LatestProviderActivityAge).IsEqualTo(TimeSpan.FromSeconds(4));
        _ = await Assert.That(captured.Recent).IsEmpty();
    }

    [Test]
    public async Task Named_and_sequential_unnamed_summaries_close_atomically()
    {
        var time = new ControlledTimeProvider();
        var activity = new AgentSessionActivity(time);

        activity.ObserveProviderEvent(Summary("named ", "part", completed: false));
        activity.ObserveProviderEvent(Summary("summary", "part", completed: false));
        _ = await Assert.That(activity.Capture().Recent).IsEmpty();
        activity.ObserveProviderEvent(Summary(string.Empty, "part", completed: true));

        activity.ObserveProviderEvent(Summary("first", string.Empty, completed: false));
        activity.ObserveProviderEvent(Summary(string.Empty, string.Empty, completed: true));
        activity.ObserveProviderEvent(Summary("second", string.Empty, completed: true));

        var captured = activity.Capture();
        _ = await Assert.That(captured.Recent).Count().IsEqualTo(3);
        _ = await Assert.That(captured.Recent[0].Content).IsEqualTo("named summary");
        _ = await Assert.That(captured.Recent[1].Content).IsEqualTo("first");
        _ = await Assert.That(captured.Recent[2].Content).IsEqualTo("second");
        _ = await Assert.That(captured.Recent.All(static entry =>
            entry.Kind == AgentSessionActivityEntryKind.ReasoningSummary)).IsTrue();
    }

    [Test]
    public async Task Request_cleanup_discards_unfinished_summaries_and_blank_completion()
    {
        var activity = new AgentSessionActivity(new ControlledTimeProvider());

        activity.ObserveProviderEvent(Summary("stale named", "reused", completed: false));
        activity.ObserveProviderEvent(Summary("stale unnamed", string.Empty, completed: false));
        activity.FinishProviderRequest();
        activity.ObserveProviderEvent(Summary("fresh named", "reused", completed: true));
        activity.ObserveProviderEvent(Summary("fresh unnamed", string.Empty, completed: true));
        activity.ObserveProviderEvent(Summary("   ", "blank", completed: true));

        var captured = activity.Capture();
        _ = await Assert.That(captured.Recent).Count().IsEqualTo(2);
        _ = await Assert.That(captured.Recent[0].Content).IsEqualTo("fresh named");
        _ = await Assert.That(captured.Recent[1].Content).IsEqualTo("fresh unnamed");
    }

    [Test]
    public async Task Request_session_duration_advances_while_active_and_stops_at_completion()
    {
        var time = new ControlledTimeProvider();
        var activity = new AgentSessionActivity(time);

        _ = await Assert.That(activity.Capture().RequestSessionDuration).IsNull();
        var execution = activity.BeginExecution();
        time.Advance(TimeSpan.FromSeconds(1));
        _ = await Assert.That(activity.Capture().RequestSessionDuration).IsEqualTo(TimeSpan.FromSeconds(1));

        activity.FinishExecution(execution, AgentExecution.Succeeded(string.Empty));
        time.Advance(TimeSpan.FromSeconds(2));

        _ = await Assert.That(activity.Capture().RequestSessionDuration).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task Older_execution_completion_does_not_finish_newer_request_session()
    {
        var time = new ControlledTimeProvider();
        var activity = new AgentSessionActivity(time);

        var older = activity.BeginExecution();
        time.Advance(TimeSpan.FromSeconds(1));
        var current = activity.BeginExecution();
        time.Advance(TimeSpan.FromSeconds(2));
        activity.FinishExecution(older, AgentExecution.Succeeded(string.Empty));

        var active = activity.Capture();
        _ = await Assert.That(active.RequestSessionDuration).IsEqualTo(TimeSpan.FromSeconds(2));
        _ = await Assert.That(active.TerminalOutcome).IsNull();

        activity.FinishExecution(current, AgentExecution.Canceled());
        _ = await Assert.That(activity.Capture().TerminalOutcome?.Status)
            .IsEqualTo(AgentExecutionStatus.Canceled);
    }

    [Test]
    public async Task Provider_request_durations_distinguish_current_and_last_completed_requests()
    {
        var time = new ControlledTimeProvider();
        var activity = new AgentSessionActivity(time);

        activity.BeginProviderRequest();
        time.Advance(TimeSpan.FromSeconds(1));
        var active = activity.Capture();
        _ = await Assert.That(active.CurrentProviderRequestDuration).IsEqualTo(TimeSpan.FromSeconds(1));
        _ = await Assert.That(active.LastProviderRequestDuration).IsNull();

        activity.FinishProviderRequest();
        time.Advance(TimeSpan.FromSeconds(2));
        var completed = activity.Capture();
        _ = await Assert.That(completed.CurrentProviderRequestDuration).IsNull();
        _ = await Assert.That(completed.LastProviderRequestDuration).IsEqualTo(TimeSpan.FromSeconds(1));

        activity.BeginProviderRequest();
        time.Advance(TimeSpan.FromSeconds(3));
        var next = activity.Capture();
        _ = await Assert.That(next.CurrentProviderRequestDuration).IsEqualTo(TimeSpan.FromSeconds(3));
        _ = await Assert.That(next.LastProviderRequestDuration).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task Assistant_and_summary_entries_share_oldest_to_newest_five_entry_bound()
    {
        var time = new ControlledTimeProvider();
        var activity = new AgentSessionActivity(time);

        activity.RecordAssistantMessage("dropped");
        activity.ObserveProviderEvent(Summary("summary-1", "one", completed: true));
        activity.RecordAssistantMessage("assistant-2");
        activity.ObserveProviderEvent(Summary("summary-3", "three", completed: true));
        activity.RecordAssistantMessage("assistant-4");
        activity.RecordAssistantMessage("assistant-5");

        var captured = activity.Capture();
        _ = await Assert.That(captured.Recent).Count().IsEqualTo(5);
        _ = await Assert.That(string.Join(',', captured.Recent.Select(static entry => entry.Content)))
            .IsEqualTo("summary-1,assistant-2,summary-3,assistant-4,assistant-5");
        _ = await Assert.That(captured.Recent[0].Kind)
            .IsEqualTo(AgentSessionActivityEntryKind.ReasoningSummary);
        _ = await Assert.That(captured.Recent[1].Kind)
            .IsEqualTo(AgentSessionActivityEntryKind.AssistantMessage);
    }

    [Test]
    public async Task Snapshot_collection_is_a_copy_without_array_mutability()
    {
        var activity = new AgentSessionActivity(new ControlledTimeProvider());
        activity.RecordAssistantMessage("first");

        var captured = activity.Capture();
        activity.RecordAssistantMessage("second");

        _ = await Assert.That(captured.Recent).IsTypeOf<ReadOnlyCollection<AgentSessionActivityEntrySnapshot>>();
        _ = await Assert.That(captured.Recent).Count().IsEqualTo(1);
        _ = await Assert.That(captured.Recent[0].Content).IsEqualTo("first");
    }

    [Test]
    public async Task Concurrent_capture_returns_coherent_bounded_snapshots()
    {
        var activity = new AgentSessionActivity(new ControlledTimeProvider());
        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 1000; index++)
            {
                activity.RecordAssistantMessage($"entry-{index}");
            }
        });
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 1000; index++)
            {
                var captured = activity.Capture();
                if (captured.Recent.Count > 5
                    || captured.Recent.Any(static entry => string.IsNullOrWhiteSpace(entry.Content)))
                {
                    throw new InvalidOperationException("activity snapshot is incoherent");
                }
            }
        }));

        await Task.WhenAll(readers.Append(writer));
        _ = await Assert.That(activity.Capture().Recent).Count().IsEqualTo(5);
    }

    [Test]
    public async Task Tool_and_lifecycle_state_clear_and_retain_terminal_outcome()
    {
        var activity = new AgentSessionActivity(new ControlledTimeProvider());

        activity.ChangeState(DrainState.Running);
        var older = activity.BeginTool("first");
        var current = activity.BeginTool("second");
        activity.FinishTool(older);
        _ = await Assert.That(activity.Capture().CurrentTool).IsEqualTo("second");
        activity.FinishTool(current);
        activity.ChangeState(DrainState.Interrupting);
        activity.ChangeState(DrainState.Idle);
        var execution = activity.BeginExecution();
        var outcome = AgentExecution.Canceled();
        activity.FinishExecution(execution, outcome);

        var terminal = activity.Capture();
        _ = await Assert.That(terminal.CurrentTool).IsNull();
        _ = await Assert.That(terminal.State).IsEqualTo(DrainState.Idle);
        _ = await Assert.That(terminal.TerminalOutcome).IsEqualTo(outcome);

        _ = activity.BeginExecution();
        _ = await Assert.That(activity.Capture().TerminalOutcome).IsNull();
    }

    [Test]
    public async Task Elapsed_ages_use_monotonic_time_and_clamp_negative_values()
    {
        var time = new ControlledTimeProvider();
        var activity = new AgentSessionActivity(time);
        activity.ObserveProviderEvent(LLMEvent.TextDelta("text"));
        activity.RecordAssistantMessage("answer");
        time.Advance(TimeSpan.FromSeconds(5));

        var advanced = activity.Capture();
        time.Advance(TimeSpan.FromSeconds(-10));
        var rewound = activity.Capture();

        _ = await Assert.That(advanced.LatestProviderActivityAge).IsEqualTo(TimeSpan.FromSeconds(5));
        _ = await Assert.That(advanced.Recent[0].Age).IsEqualTo(TimeSpan.FromSeconds(5));
        _ = await Assert.That(rewound.LatestProviderActivityAge).IsEqualTo(TimeSpan.Zero);
        _ = await Assert.That(rewound.Recent[0].Age).IsEqualTo(TimeSpan.Zero);
    }

    private static LLMEvent Summary(string fragment, string partId, bool completed) =>
        LLMEvent.ReasoningDelta(fragment, LLMReasoningKind.Summary, partId, completed);
}
