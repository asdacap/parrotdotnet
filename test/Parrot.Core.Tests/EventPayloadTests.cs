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
    public async Task Plan_validation_repair_roundtrips_as_additive_payload()
    {
        var source = new Event { PlanValidationRepairInjected = new PlanValidationRepairInjected { Diagnostic = "repair tasks" } };
        var bytes = source.ToByteArray();
        var restored = Event.Parser.ParseFrom(bytes);
        _ = await Assert.That(restored.PayloadCase).IsEqualTo(Event.PayloadOneofCase.PlanValidationRepairInjected);
        _ = await Assert.That(restored.PlanValidationRepairInjected.Diagnostic).IsEqualTo("repair tasks");
    }

    [Test]
    public async Task Plan_completed_task_tree_roundtrips_and_absence_remains_valid()
    {
        var source = new PlanCompleted
        {
            AgentSessionId = "session",
            MessageId = "message",
            Markdown = "# Plan",
            TaskTree = new AgentTaskProgressSnapshot
            {
                RootNodes =
                {
                    new AgentTaskProgressNode
                    {
                        Name = "first-id",
                        Description = "first description",
                        Status = AgentTaskProgressStatus.Pending,
                        Children =
                        {
                            new AgentTaskProgressNode { Name = "child-id", Description = "child description", Status = AgentTaskProgressStatus.Pending },
                        },
                    },
                    new AgentTaskProgressNode { Name = "second-id", Description = "second description", Status = AgentTaskProgressStatus.Pending },
                },
            },
        };

        var roundtripped = PlanCompleted.Parser.ParseFrom(source.ToByteArray());
        var absent = PlanCompleted.Parser.ParseFrom(new PlanCompleted { Markdown = "# Plan" }.ToByteArray());

        _ = await Assert.That(roundtripped.TaskTree).IsNotNull();
        _ = await Assert.That(string.Join(',', roundtripped.TaskTree.RootNodes.Select(node => node.Name)))
            .IsEqualTo("first-id,second-id");
        _ = await Assert.That(roundtripped.TaskTree.RootNodes[0].Description).IsEqualTo("first description");
        _ = await Assert.That(roundtripped.TaskTree.RootNodes[0].Children[0].Name).IsEqualTo("child-id");
        _ = await Assert.That(roundtripped.TaskTree.RootNodes[0].Children[0].Description).IsEqualTo("child description");
        _ = await Assert.That(roundtripped.TaskTree.RootNodes[0].Status).IsEqualTo(AgentTaskProgressStatus.Pending);
        _ = await Assert.That(absent.TaskTree).IsNull();
    }

    [Test]
    public async Task Plan_completed_task_declarations_roundtrip_preserves_nested_order_and_fields()
    {
        var source = new PlanCompleted
        {
            Markdown = "# Plan",
            TaskDeclarations =
            {
                new PlanTaskDeclaration
                {
                    Name = "composite",
                    Status = AgentTaskProgressStatus.Pending,
                    Dependencies = { "first", "second" },
                    Description = "Composite description",
                    AcceptanceCriteria = "Composite criteria",
                    Model = "model/composite",
                    Children = new PlanTaskDeclarationChildren
                    {
                        Tasks =
                        {
                            new PlanTaskDeclaration
                            {
                                Name = "leaf",
                                Status = AgentTaskProgressStatus.Pending,
                                Dependencies = { "nested-dependency" },
                                Description = "Leaf description",
                                AcceptanceCriteria = "Leaf criteria",
                                Instruction = "Leaf instruction",
                            },
                            new PlanTaskDeclaration
                            {
                                Name = "leaf-without-model",
                                Status = AgentTaskProgressStatus.Pending,
                                Description = "Second leaf description",
                                AcceptanceCriteria = "Second leaf criteria",
                                Instruction = "Second instruction",
                            },
                        },
                    },
                },
                new PlanTaskDeclaration
                {
                    Name = "second",
                    Status = AgentTaskProgressStatus.Pending,
                    Description = "Second description",
                    AcceptanceCriteria = "Second criteria",
                    Instruction = "Second instruction",
                },
            },
        };

        var roundtripped = PlanCompleted.Parser.ParseFrom(source.ToByteArray());
        var composite = roundtripped.TaskDeclarations[0];
        var leaf = composite.Children.Tasks[0];
        var omittedModel = composite.Children.Tasks[1];

        _ = await Assert.That(string.Join(',', roundtripped.TaskDeclarations.Select(task => task.Name)))
            .IsEqualTo("composite,second");
        _ = await Assert.That(composite.Status).IsEqualTo(AgentTaskProgressStatus.Pending);
        _ = await Assert.That(composite.Dependencies.SequenceEqual(["first", "second"])).IsTrue();
        _ = await Assert.That(composite.Description).IsEqualTo("Composite description");
        _ = await Assert.That(composite.AcceptanceCriteria).IsEqualTo("Composite criteria");
        _ = await Assert.That(composite.HasModel).IsTrue();
        _ = await Assert.That(composite.Model).IsEqualTo("model/composite");
        _ = await Assert.That(leaf.PayloadCase).IsEqualTo(PlanTaskDeclaration.PayloadOneofCase.Instruction);
        _ = await Assert.That(leaf.Instruction).IsEqualTo("Leaf instruction");
        _ = await Assert.That(leaf.Dependencies.Single()).IsEqualTo("nested-dependency");
        _ = await Assert.That(omittedModel.PayloadCase).IsEqualTo(PlanTaskDeclaration.PayloadOneofCase.Instruction);
        _ = await Assert.That(omittedModel.HasModel).IsFalse();
        _ = await Assert.That(omittedModel.Model).IsEqualTo(string.Empty);
    }

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
    public async Task Provider_call_usage_roundtrips_as_additive_field_thirty_five_with_exact_counts()
    {
        var source = new Event
        {
            ProviderCallUsage = new ProviderCallUsage
            {
                InputTokens = 4_294_967_296,
                OutputTokens = 8_589_934_592,
            },
        };

        var bytes = source.ToByteArray();
        var roundtripped = Event.Parser.ParseFrom(bytes);

        _ = await Assert.That(roundtripped.PayloadCase).IsEqualTo(Event.PayloadOneofCase.ProviderCallUsage);
        _ = await Assert.That(roundtripped.ProviderCallUsage.InputTokens).IsEqualTo(4_294_967_296L);
        _ = await Assert.That(roundtripped.ProviderCallUsage.OutputTokens).IsEqualTo(8_589_934_592L);
        _ = await Assert.That(bytes[0]).IsEqualTo((byte)0x9a);
        _ = await Assert.That(bytes[1]).IsEqualTo((byte)0x02);
    }

    [Test]
    public async Task Agent_task_progress_snapshot_roundtrips_as_field_thirty_four()
    {
        var source = new Event
        {
            Id = "agent-task-progress-event",
            AgentSessionId = "agent-session",
            AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
            {
                OriginToolCallId = "tool-call",
                Revision = 19,
                RootNodes =
                {
                    new AgentTaskProgressNode
                    {
                        Name = "pending-root",
                        Status = AgentTaskProgressStatus.Pending,
                        Children =
                        {
                            new AgentTaskProgressNode
                            {
                                Name = "running-child",
                                Status = AgentTaskProgressStatus.Running,
                                Children =
                                {
                                    new AgentTaskProgressNode
                                    {
                                        Name = "succeeded-grandchild",
                                        Status = AgentTaskProgressStatus.Succeeded,
                                    },
                                    new AgentTaskProgressNode
                                    {
                                        Name = "failed-grandchild",
                                        Status = AgentTaskProgressStatus.Failed,
                                    },
                                },
                            },
                        },
                    },
                    new AgentTaskProgressNode
                    {
                        Name = "blocked-root",
                        Status = AgentTaskProgressStatus.Blocked,
                    },
                    new AgentTaskProgressNode
                    {
                        Name = "canceled-root",
                        Status = AgentTaskProgressStatus.Canceled,
                    },
                },
            },
        };

        var bytes = source.ToByteArray();
        var roundtripped = Event.Parser.ParseFrom(bytes);
        var snapshot = roundtripped.AgentTaskProgressSnapshot;
        var pendingRoot = snapshot.RootNodes[0];
        var runningChild = pendingRoot.Children[0];

        _ = await Assert.That(roundtripped.Id).IsEqualTo("agent-task-progress-event");
        _ = await Assert.That(roundtripped.AgentSessionId).IsEqualTo("agent-session");
        _ = await Assert.That(roundtripped.PayloadCase).IsEqualTo(Event.PayloadOneofCase.AgentTaskProgressSnapshot);
        _ = await Assert.That(snapshot.OriginToolCallId).IsEqualTo("tool-call");
        _ = await Assert.That(snapshot.Revision).IsEqualTo(19UL);
        _ = await Assert.That(snapshot.RootNodes).Count().IsEqualTo(3);
        _ = await Assert.That(pendingRoot.Name).IsEqualTo("pending-root");
        _ = await Assert.That(pendingRoot.Status).IsEqualTo(AgentTaskProgressStatus.Pending);
        _ = await Assert.That(snapshot.RootNodes[1].Name).IsEqualTo("blocked-root");
        _ = await Assert.That(snapshot.RootNodes[1].Status).IsEqualTo(AgentTaskProgressStatus.Blocked);
        _ = await Assert.That(snapshot.RootNodes[2].Name).IsEqualTo("canceled-root");
        _ = await Assert.That(snapshot.RootNodes[2].Status).IsEqualTo(AgentTaskProgressStatus.Canceled);
        _ = await Assert.That(pendingRoot.Children).Count().IsEqualTo(1);
        _ = await Assert.That(runningChild.Name).IsEqualTo("running-child");
        _ = await Assert.That(runningChild.Status).IsEqualTo(AgentTaskProgressStatus.Running);
        _ = await Assert.That(runningChild.Children).Count().IsEqualTo(2);
        _ = await Assert.That(runningChild.Children[0].Name).IsEqualTo("succeeded-grandchild");
        _ = await Assert.That(runningChild.Children[0].Status).IsEqualTo(AgentTaskProgressStatus.Succeeded);
        _ = await Assert.That(runningChild.Children[1].Name).IsEqualTo("failed-grandchild");
        _ = await Assert.That(runningChild.Children[1].Status).IsEqualTo(AgentTaskProgressStatus.Failed);
        _ = await Assert.That(bytes[42]).IsEqualTo((byte)0x92);
        _ = await Assert.That(bytes[43]).IsEqualTo((byte)0x02);
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
                RootAgentSessionId = "root-session",
                Queues =
                {
                    new QueueState
                    {
                        OwnerAgentSessionId = "agent-session",
                        OwnerAgentName = "worker",
                        ParentAgentSessionId = "root-session",
                        ParentAgentName = "main",
                        Name = "release",
                        Description = "release tasks",
                        ItemCount = 3,
                    },
                },
            },
        };

        var bytes = source.ToByteArray();
        var roundtripped = Event.Parser.ParseFrom(bytes);

        _ = await Assert.That(roundtripped.PayloadCase).IsEqualTo(Event.PayloadOneofCase.QueueSnapshot);
        _ = await Assert.That(roundtripped.QueueSnapshot.Revision).IsEqualTo(7UL);
        _ = await Assert.That(roundtripped.QueueSnapshot.ChunkIndex).IsEqualTo(2U);
        _ = await Assert.That(roundtripped.QueueSnapshot.FinalChunk).IsTrue();
        _ = await Assert.That(roundtripped.QueueSnapshot.RootAgentSessionId).IsEqualTo("root-session");
        _ = await Assert.That(roundtripped.QueueSnapshot.Queues[0].OwnerAgentSessionId)
            .IsEqualTo("agent-session");
        _ = await Assert.That(roundtripped.QueueSnapshot.Queues[0].OwnerAgentName).IsEqualTo("worker");
        _ = await Assert.That(roundtripped.QueueSnapshot.Queues[0].ParentAgentSessionId)
            .IsEqualTo("root-session");
        _ = await Assert.That(roundtripped.QueueSnapshot.Queues[0].ParentAgentName).IsEqualTo("main");
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
                    StdoutPath = "/state/stdout",
                    StderrPath = "/state/stderr",
                },
            },
        };

        var roundtripped = Event.Parser.ParseFrom(source.ToByteArray());

        _ = await Assert.That(roundtripped.ToolFinished.Result).IsEqualTo("compile");
        _ = await Assert.That(roundtripped.ToolFinished.YieldedProcess.ProcessId).IsEqualTo("process-1");
        _ = await Assert.That(roundtripped.ToolFinished.YieldedProcess.InventoryInstanceId).IsEqualTo("inventory-1");
        _ = await Assert.That(roundtripped.ToolFinished.YieldedProcess.VisibleRevision).IsEqualTo(3UL);
        _ = await Assert.That(roundtripped.ToolFinished.YieldedProcess.HasStdoutPath).IsTrue();
        _ = await Assert.That(roundtripped.ToolFinished.YieldedProcess.StdoutPath).IsEqualTo("/state/stdout");
        _ = await Assert.That(roundtripped.ToolFinished.YieldedProcess.HasStderrPath).IsTrue();
        _ = await Assert.That(roundtripped.ToolFinished.YieldedProcess.StderrPath).IsEqualTo("/state/stderr");

        var pathless = Event.Parser.ParseFrom(new Event
        {
            ToolFinished = new ToolFinished
            {
                YieldedProcess = new YieldedShellProcess { ProcessId = "process-2" },
            },
        }.ToByteArray());
        _ = await Assert.That(pathless.ToolFinished.YieldedProcess.HasStdoutPath).IsFalse();
        _ = await Assert.That(pathless.ToolFinished.YieldedProcess.HasStderrPath).IsFalse();
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
    public async Task Compaction_lifecycle_roundtrips_as_protobuf_payloads()
    {
        var started = Event.Parser.ParseFrom(new Event
        {
            Id = "compaction-started",
            AgentSessionId = "session",
            CompactionStarted = new CompactionStarted(),
        }.ToByteArray());
        var finished = Event.Parser.ParseFrom(new Event
        {
            Id = "compaction-finished",
            AgentSessionId = "session",
            CompactionFinished = new CompactionFinished(),
        }.ToByteArray());
        var failed = Event.Parser.ParseFrom(new Event
        {
            Id = "compaction-failed",
            AgentSessionId = "session",
            CompactionFailed = new CompactionFailed { Message = "summary failed" },
        }.ToByteArray());

        _ = await Assert.That(started.PayloadCase).IsEqualTo(Event.PayloadOneofCase.CompactionStarted);
        _ = await Assert.That(finished.PayloadCase).IsEqualTo(Event.PayloadOneofCase.CompactionFinished);
        _ = await Assert.That(failed.PayloadCase).IsEqualTo(Event.PayloadOneofCase.CompactionFailed);
        _ = await Assert.That(failed.CompactionFailed.Message).IsEqualTo("summary failed");
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
    public async Task Provider_request_limit_prompts_roundtrip_as_distinct_protobuf_payloads()
    {
        var finalRequest = Event.Parser.ParseFrom(new Event
        {
            Id = "final-provider-request-event",
            AgentSessionId = "session",
            FinalProviderRequestPromptInjected = new FinalProviderRequestPromptInjected(),
        }.ToByteArray());
        var restored = Event.Parser.ParseFrom(new Event
        {
            Id = "tool-availability-restored-event",
            AgentSessionId = "session",
            ToolAvailabilityRestoredPromptInjected = new ToolAvailabilityRestoredPromptInjected(),
        }.ToByteArray());

        _ = await Assert.That(finalRequest.PayloadCase)
            .IsEqualTo(Event.PayloadOneofCase.FinalProviderRequestPromptInjected);
        _ = await Assert.That(restored.PayloadCase)
            .IsEqualTo(Event.PayloadOneofCase.ToolAvailabilityRestoredPromptInjected);
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
    public async Task Provider_request_limit_prompts_are_durable_and_restore_exactly_once()
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var finalRequest = new Event { Id = "final", AgentSessionId = "session" };
        var restored = new Event { Id = "restored", AgentSessionId = "session" };

        repository.AppendFinalProviderRequestPrompt(finalRequest, "tools unavailable");
        var firstRestoration = repository.AppendToolAvailabilityRestoredPrompt(restored, "tools restored");
        var secondRestoration = repository.AppendToolAvailabilityRestoredPrompt(
            new Event { Id = "duplicate", AgentSessionId = "session" },
            "duplicate restoration");

        _ = await Assert.That(finalRequest.PayloadCase)
            .IsEqualTo(Event.PayloadOneofCase.FinalProviderRequestPromptInjected);
        _ = await Assert.That(restored.PayloadCase)
            .IsEqualTo(Event.PayloadOneofCase.ToolAvailabilityRestoredPromptInjected);
        _ = await Assert.That(firstRestoration).IsTrue();
        _ = await Assert.That(secondRestoration).IsFalse();
        _ = await Assert.That(string.Join(" | ", repository.Messages("session")))
            .IsEqualTo("system: tools unavailable | system: tools restored");
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

    // The payload is the only discriminator, so this pins the provider-event mapping through publication.
    [Test]
    [Arguments(LLMEventKind.TextDelta, Event.PayloadOneofCase.TextChunk)]
    [Arguments(LLMEventKind.ReasoningDelta, Event.PayloadOneofCase.ReasoningChunk)]
    [Arguments(LLMEventKind.ToolCallDelta, Event.PayloadOneofCase.ToolCallChunk)]
    [Arguments(LLMEventKind.Retry, Event.PayloadOneofCase.RetryNotice)]
    public async Task Each_llm_event_maps_to_a_published_payload(
        LLMEventKind source,
        Event.PayloadOneofCase expectedPayload,
        CancellationToken cancellationToken)
    {
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var llmEvent = source switch
        {
            LLMEventKind.TextDelta => LLMEvent.TextDelta("fragment"),
            LLMEventKind.ReasoningDelta => LLMEvent.ReasoningDelta(
                "thought", LLMReasoningKind.Summary, "reasoning-1", completed: true),
            LLMEventKind.ToolCallDelta => LLMEvent.ToolCallDelta("call", "glob", "{}"),
            _ => LLMEvent.Retry(2, TimeSpan.FromSeconds(1), "429"),
        };
        var provider = new PayloadProvider(llmEvent);
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var identity = AgentIdentity.Main("session", string.Empty, TestModels.PromptTemplates);
        var repository = new EventRepository(database);
        using var dependencies = TestModels.Dependencies(identity, events, repository, cancellationToken);
        using var subscription = events.Subscribe();
        var session = new AgentSession(identity, AgentSessionParentScope.Root(), new ModelSelector(model.Selector), TestModels.Route(model), events, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(Path.GetTempPath()), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), TestModels.PromptTemplates, dependencies.ChildQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, dependencies.Profile, SecurityProfileTestFactory.Create(SecurityProfile.Compose(readOnly: false, [], [], [])), dependencies.Status, dependencies.ChildRegistry, dependencies.Queues, new AgentSessionActivity(TimeProvider.System), CancellationToken.None);

        _ = await session.Send(
            [ConversationPart.TextPart("prompt")], "message", Delivery.Steer, cancellationToken);
        await session.Settled();
        var published = new List<Event>();
        while (subscription.Reader.TryRead(out var next))
        {
            published.Add(next);
        }

        var mapped = published.Single(item => item.PayloadCase == expectedPayload);
        _ = await Assert.That(mapped.AgentSessionId).IsEqualTo("session");
        _ = await Assert.That(mapped.Id).IsNotEmpty();

        if (source == LLMEventKind.ReasoningDelta)
        {
            _ = await Assert.That(mapped.ReasoningChunk.Kind).IsEqualTo(ReasoningKind.Summary);
            _ = await Assert.That(mapped.ReasoningChunk.PartId).IsEqualTo("reasoning-1");
            _ = await Assert.That(mapped.ReasoningChunk.Completed).IsTrue();
        }
    }

    private sealed class PayloadProvider(LLMEvent llmEvent) : ILLMProvider
    {
        public string Id => "payload";

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
            throw new NotSupportedException("the payload test does not list models");

        public async IAsyncEnumerable<LLMEvent> Call(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return llmEvent;
            yield return LLMEvent.Completed("stop", 1, 0, 1, string.Empty, []);
        }
    }
}
