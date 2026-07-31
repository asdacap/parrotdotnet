using Google.Protobuf;
using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class EventPayloadTests
{
    [Test]
    public async Task Agent_statistics_roundtrip_as_a_protobuf_payload()
    {
        var source = new Event
        {
            Id = "statistics-event",
            AgentSessionId = "session",
            AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
            {
                InputTokens = 4_000_000_000,
                CachedInputTokens = 3_000_000_000,
                OutputTokens = 2_000_000_000,
                ContextSize = 100_000,
                ContextLimit = 500_000,
                InputCost = 12.5,
                OutputCost = 7.25,
            },
        };

        var roundtripped = Event.Parser.ParseFrom(source.ToByteArray());

        _ = await Assert.That(roundtripped.Id).IsEqualTo("statistics-event");
        _ = await Assert.That(roundtripped.AgentSessionId).IsEqualTo("session");
        _ = await Assert.That(roundtripped.PayloadCase).IsEqualTo(Event.PayloadOneofCase.AgentStatisticsUpdated);
        _ = await Assert.That(roundtripped.AgentStatisticsUpdated.InputTokens).IsEqualTo(4_000_000_000);
        _ = await Assert.That(roundtripped.AgentStatisticsUpdated.CachedInputTokens).IsEqualTo(3_000_000_000);
        _ = await Assert.That(roundtripped.AgentStatisticsUpdated.OutputTokens).IsEqualTo(2_000_000_000);
        _ = await Assert.That(roundtripped.AgentStatisticsUpdated.ContextSize).IsEqualTo(100_000);
        _ = await Assert.That(roundtripped.AgentStatisticsUpdated.ContextLimit).IsEqualTo(500_000);
        _ = await Assert.That(roundtripped.AgentStatisticsUpdated.InputCost).IsEqualTo(12.5);
        _ = await Assert.That(roundtripped.AgentStatisticsUpdated.OutputCost).IsEqualTo(7.25);
    }

    [Test]
    public async Task Session_usage_snapshot_roundtrips_as_field_twenty_seven()
    {
        var source = new Event
        {
            SessionUsageSnapshot = new SessionUsageSnapshot
            {
                Revision = 42,
                InputTokens = 4_000_000_000,
                CachedInputTokens = 3_000_000_000,
                OutputTokens = 2_000_000_000,
                ContextSize = 100_000,
                ContextLimit = 500_000,
                InputCost = 12.5,
                OutputCost = 7.25,
            },
        };

        var bytes = source.ToByteArray();
        var roundtripped = Event.Parser.ParseFrom(bytes);

        _ = await Assert.That(roundtripped.PayloadCase).IsEqualTo(Event.PayloadOneofCase.SessionUsageSnapshot);
        _ = await Assert.That(roundtripped.SessionUsageSnapshot.Revision).IsEqualTo(42UL);
        _ = await Assert.That(roundtripped.SessionUsageSnapshot.InputTokens).IsEqualTo(4_000_000_000);
        _ = await Assert.That(roundtripped.SessionUsageSnapshot.CachedInputTokens).IsEqualTo(3_000_000_000);
        _ = await Assert.That(roundtripped.SessionUsageSnapshot.OutputTokens).IsEqualTo(2_000_000_000);
        _ = await Assert.That(roundtripped.SessionUsageSnapshot.ContextSize).IsEqualTo(100_000);
        _ = await Assert.That(roundtripped.SessionUsageSnapshot.ContextLimit).IsEqualTo(500_000);
        _ = await Assert.That(roundtripped.SessionUsageSnapshot.InputCost).IsEqualTo(12.5);
        _ = await Assert.That(roundtripped.SessionUsageSnapshot.OutputCost).IsEqualTo(7.25);
        _ = await Assert.That(bytes[0]).IsEqualTo((byte)0xda);
        _ = await Assert.That(bytes[1]).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task Pending_permission_roundtrips_as_a_protobuf_payload()
    {
        var source = new Event
        {
            Id = "permission-event",
            AgentSessionId = "agent-session",
            PermissionPending = new PendingPermission
            {
                Id = "permission-request",
                AgentSessionId = "agent-session",
                Reason = "update generated files",
                Targets =
                {
                    new PermissionTarget
                    {
                        Kind = PermissionTargetKind.Directory,
                        Scope = PermissionTargetScope.Write,
                        Path = "/project/generated",
                    },
                },
                Choices =
                {
                    new PermissionChoice
                    {
                        Value = "allow",
                        Label = "Allow",
                        Action = PermissionAction.Allow,
                    },
                    new PermissionChoice
                    {
                        Value = "deny",
                        Label = "Deny",
                        Action = PermissionAction.Deny,
                        RequiresReason = true,
                    },
                },
            },
        };

        var roundtripped = Event.Parser.ParseFrom(source.ToByteArray());

        _ = await Assert.That(roundtripped.PayloadCase).IsEqualTo(Event.PayloadOneofCase.PermissionPending);
        _ = await Assert.That(roundtripped.PermissionPending.Id).IsEqualTo("permission-request");
        _ = await Assert.That(roundtripped.PermissionPending.AgentSessionId).IsEqualTo("agent-session");
        _ = await Assert.That(roundtripped.PermissionPending.Reason).IsEqualTo("update generated files");
        _ = await Assert.That(roundtripped.PermissionPending.Targets[0].Kind)
            .IsEqualTo(PermissionTargetKind.Directory);
        _ = await Assert.That(roundtripped.PermissionPending.Targets[0].Scope)
            .IsEqualTo(PermissionTargetScope.Write);
        _ = await Assert.That(roundtripped.PermissionPending.Targets[0].Path).IsEqualTo("/project/generated");
        _ = await Assert.That(roundtripped.PermissionPending.Choices[0].Action).IsEqualTo(PermissionAction.Allow);
        _ = await Assert.That(roundtripped.PermissionPending.Choices[1].Action).IsEqualTo(PermissionAction.Deny);
        _ = await Assert.That(roundtripped.PermissionPending.Choices[1].RequiresReason).IsTrue();
    }

    [Test]
    public async Task Queue_snapshot_roundtrips_as_field_twenty_four()
    {
        var source = new Event
        {
            QueueSnapshot = new QueueSnapshot
            {
                Revision = 7,
                ChunkIndex = 2,
                FinalChunk = true,
                Queues =
                {
                    new QueueState { Name = "release", Description = "release tasks", ItemCount = 3 },
                },
            },
        };

        var bytes = source.ToByteArray();
        var roundtripped = Event.Parser.ParseFrom(bytes);

        _ = await Assert.That(roundtripped.PayloadCase).IsEqualTo(Event.PayloadOneofCase.QueueSnapshot);
        _ = await Assert.That(roundtripped.QueueSnapshot.Revision).IsEqualTo(7UL);
        _ = await Assert.That(roundtripped.QueueSnapshot.ChunkIndex).IsEqualTo(2U);
        _ = await Assert.That(roundtripped.QueueSnapshot.FinalChunk).IsTrue();
        _ = await Assert.That(roundtripped.QueueSnapshot.Queues[0].Name).IsEqualTo("release");
        _ = await Assert.That(roundtripped.QueueSnapshot.Queues[0].Description).IsEqualTo("release tasks");
        _ = await Assert.That(roundtripped.QueueSnapshot.Queues[0].ItemCount).IsEqualTo(3);
        _ = await Assert.That(bytes[0]).IsEqualTo((byte)0xc2);
        _ = await Assert.That(bytes[1]).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task Shell_process_snapshot_roundtrips_as_field_twenty_five()
    {
        var source = new Event
        {
            ShellProcessSnapshot = new ShellProcessSnapshot
            {
                InventoryInstanceId = "inventory-1",
                Revision = 9,
                ChunkIndex = 0,
                ChunkCount = 1,
                Processes =
                {
                    new ActiveShellProcess
                    {
                        ProcessId = "process-1",
                        Name = "compile",
                        Command = "dotnet build",
                        OriginToolCallId = "call-1",
                        OwnerAgentSessionId = "agent-1",
                        OwnerAgentName = "main",
                        ParentAgentSessionId = "parent-1",
                        ParentAgentName = "parent",
                        Depth = 2,
                        ElapsedMs = 42,
                    },
                },
            },
        };

        var bytes = source.ToByteArray();
        var roundtripped = Event.Parser.ParseFrom(bytes);

        _ = await Assert.That(roundtripped.PayloadCase).IsEqualTo(Event.PayloadOneofCase.ShellProcessSnapshot);
        _ = await Assert.That(roundtripped.ShellProcessSnapshot.InventoryInstanceId).IsEqualTo("inventory-1");
        _ = await Assert.That(roundtripped.ShellProcessSnapshot.Revision).IsEqualTo(9UL);
        _ = await Assert.That(roundtripped.ShellProcessSnapshot.ChunkCount).IsEqualTo(1U);
        _ = await Assert.That(roundtripped.ShellProcessSnapshot.Processes[0].ProcessId).IsEqualTo("process-1");
        _ = await Assert.That(roundtripped.ShellProcessSnapshot.Processes[0].Command).IsEqualTo("dotnet build");
        _ = await Assert.That(bytes[0]).IsEqualTo((byte)0xca);
        _ = await Assert.That(bytes[1]).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task Tool_finished_roundtrips_a_yielded_process_handoff()
    {
        var source = new Event
        {
            ToolFinished = new ToolFinished
            {
                ToolCallId = "call-1",
                ToolName = "exec_command",
                Result = "compile",
                YieldedProcess = new YieldedShellProcess
                {
                    ProcessId = "process-1",
                    Name = "compile",
                    InventoryInstanceId = "inventory-1",
                    VisibleRevision = 3,
                },
            },
        };

        var roundtripped = Event.Parser.ParseFrom(source.ToByteArray());

        _ = await Assert.That(roundtripped.ToolFinished.Result).IsEqualTo("compile");
        _ = await Assert.That(roundtripped.ToolFinished.YieldedProcess.ProcessId).IsEqualTo("process-1");
        _ = await Assert.That(roundtripped.ToolFinished.YieldedProcess.InventoryInstanceId).IsEqualTo("inventory-1");
        _ = await Assert.That(roundtripped.ToolFinished.YieldedProcess.VisibleRevision).IsEqualTo(3UL);
    }

    [Test]
    public async Task Status_injected_roundtrips_as_a_protobuf_payload()
    {
        var source = new Event
        {
            Id = "status-event",
            AgentSessionId = "session",
            StatusInjected = new StatusInjected(),
        };

        var roundtripped = Event.Parser.ParseFrom(source.ToByteArray());

        _ = await Assert.That(roundtripped.Id).IsEqualTo("status-event");
        _ = await Assert.That(roundtripped.AgentSessionId).IsEqualTo("session");
        _ = await Assert.That(roundtripped.PayloadCase).IsEqualTo(Event.PayloadOneofCase.StatusInjected);
    }

    [Test]
    public async Task Active_work_reminder_injected_roundtrips_as_a_distinct_protobuf_payload()
    {
        var source = new Event
        {
            Id = "reminder-event",
            AgentSessionId = "session",
            ActiveWorkReminderInjected = new ActiveWorkReminderInjected(),
        };

        var roundtripped = Event.Parser.ParseFrom(source.ToByteArray());

        _ = await Assert.That(roundtripped.Id).IsEqualTo("reminder-event");
        _ = await Assert.That(roundtripped.AgentSessionId).IsEqualTo("session");
        _ = await Assert.That(roundtripped.PayloadCase)
            .IsEqualTo(Event.PayloadOneofCase.ActiveWorkReminderInjected);
    }

    [Test]
    public async Task Active_work_reminder_is_durable_history_with_a_transient_event()
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var published = new Event { Id = "reminder-event", AgentSessionId = "session" };

        repository.AppendActiveWorkReminder(published, "wait for direct active work");

        _ = await Assert.That(published.PayloadCase)
            .IsEqualTo(Event.PayloadOneofCase.ActiveWorkReminderInjected);
        var messages = repository.Messages("session");
        _ = await Assert.That(messages).Count().IsEqualTo(1);
        _ = await Assert.That(messages[0]).IsEqualTo("system: wait for direct active work");
        _ = await Assert.That(repository.Replay()).IsEmpty();
    }

    [Test]
    public async Task Tool_finished_result_preserves_absent_empty_and_nonempty_presence()
    {
        var absent = new Event { ToolFinished = new ToolFinished() };
        var empty = new Event { ToolFinished = new ToolFinished { Result = string.Empty } };
        var nonempty = new Event { ToolFinished = new ToolFinished { Result = "done" } };

        var roundtrippedAbsent = Event.Parser.ParseFrom(absent.ToByteArray());
        var roundtrippedEmpty = Event.Parser.ParseFrom(empty.ToByteArray());
        var roundtrippedNonempty = Event.Parser.ParseFrom(nonempty.ToByteArray());

        _ = await Assert.That(roundtrippedAbsent.ToolFinished.HasResult).IsFalse();
        _ = await Assert.That(roundtrippedEmpty.ToolFinished.HasResult).IsTrue();
        _ = await Assert.That(roundtrippedEmpty.ToolFinished.Result).IsEqualTo(string.Empty);
        _ = await Assert.That(roundtrippedNonempty.ToolFinished.HasResult).IsTrue();
        _ = await Assert.That(roundtrippedNonempty.ToolFinished.Result).IsEqualTo("done");
    }

    // The payload is the only discriminator, so this is what pins the mapping.
    [Test]
    [Arguments(LLMEventKind.TextDelta, Event.PayloadOneofCase.TextChunk)]
    [Arguments(LLMEventKind.ReasoningDelta, Event.PayloadOneofCase.ReasoningChunk)]
    [Arguments(LLMEventKind.ToolCallDelta, Event.PayloadOneofCase.ToolCallChunk)]
    [Arguments(LLMEventKind.Retry, Event.PayloadOneofCase.RetryNotice)]
    public async Task Each_llm_event_maps_to_a_payload(
        LLMEventKind source,
        Event.PayloadOneofCase expectedPayload)
    {
        using var events = new EventBroker();

        // A real repository over an in-memory database, not a null: the code
        // under test should take the same path production does.
        using var database = SessionDatabase.Open(":memory:");
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var session = new AgentSession(
            AgentIdentity.Main("session", string.Empty),
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            events,
            new EventRepository(database),
            [],
            TestModels.PromptProvider(".", "."),
            new TodoCollection("session", new EventRepository(database), events),
            new ToolOutputBlobStore(Path.GetTempPath()),
            new Compactor(120_000),
            activeWorkReminder: null,
            profile: null,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            status: null,
            registry: null,
            queues: TestModels.Queues(AgentIdentity.Main("session", string.Empty)),
            CancellationToken.None);

        var llmEvent = source switch
        {
            LLMEventKind.TextDelta => LLMEvent.TextDelta("fragment"),
            LLMEventKind.ReasoningDelta => LLMEvent.ReasoningDelta(
                "thought", LLMReasoningKind.Summary, "reasoning-1", completed: true),
            LLMEventKind.ToolCallDelta => LLMEvent.ToolCallDelta("call", "grep", "{}"),
            _ => LLMEvent.Retry(2, TimeSpan.FromSeconds(1), "429"),
        };

        var published = session.Translate(llmEvent);

        _ = await Assert.That(published.PayloadCase).IsEqualTo(expectedPayload);
        _ = await Assert.That(published.AgentSessionId).IsEqualTo("session");
        _ = await Assert.That(published.Id).IsNotEmpty();

        if (source == LLMEventKind.ReasoningDelta)
        {
            _ = await Assert.That(published.ReasoningChunk.Kind).IsEqualTo(ReasoningKind.Summary);
            _ = await Assert.That(published.ReasoningChunk.PartId).IsEqualTo("reasoning-1");
            _ = await Assert.That(published.ReasoningChunk.Completed).IsTrue();
        }
    }
}
