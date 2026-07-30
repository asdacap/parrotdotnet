using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class BasicCliTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Startup_logs_when_an_existing_session_is_loaded(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced);
        driver.Invoker.SessionLoaded = true;

        var driving = driver.Drive(cancellationToken);
        await driver.OutputContains("Loaded session session-1", cancellationToken);
        driver.Input.End();
        _ = await driving;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Startup_warns_for_unconfigured_aliases_in_name_order(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced);
        driver.Invoker.ModelAliases.Add(new ModelAlias { Name = "z_llm", Usage = "last" });
        driver.Invoker.ModelAliases.Add(new ModelAlias
        {
            Name = "configured_llm",
            ModelString = "provider/model",
            Usage = "configured",
        });
        driver.Invoker.ModelAliases.Add(new ModelAlias { Name = "a_llm", Usage = "first" });

        var driving = driver.Drive(cancellationToken);
        await driver.OutputContains("warning: model alias \"z_llm\" is not configured", cancellationToken);
        driver.Input.End();
        _ = await driving;

        var first = driver.Output.IndexOf(
            "warning: model alias \"a_llm\" is not configured", StringComparison.Ordinal);
        var last = driver.Output.IndexOf(
            "warning: model alias \"z_llm\" is not configured", StringComparison.Ordinal);
        _ = await Assert.That(first).IsGreaterThanOrEqualTo(0);
        _ = await Assert.That(last).IsGreaterThan(first);
        _ = await Assert.That(driver.Output).DoesNotContain(
            "warning: model alias \"configured_llm\" is not configured");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Pending_permission_wakes_input_and_replies_with_the_typed_choice(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced);
        var running = driver.Drive(cancellationToken);
        driver.Input.Type("work");
        await driver.Sent(1, cancellationToken);

        var pending = Permission("permission-1", requiresReason: false);
        driver.Invoker.AddPendingPermission(pending);
        await driver.Invoker.Publish(new Event { PermissionPending = pending.Clone() });
        await driver.OutputContains("Allow this write", cancellationToken);
        driver.Input.Type(enhanced ? string.Empty : "allow");

        while (driver.Invoker.PermissionReplies.Count == 0)
        {
            await Task.Delay(5, cancellationToken);
        }

        var reply = driver.Invoker.PermissionReplies.Single();
        _ = await Assert.That(reply.PermissionRequestId).IsEqualTo("permission-1");
        _ = await Assert.That(reply.ChoiceValue).IsEqualTo("allow");
        _ = await Assert.That(reply.Reason).IsEmpty();
        _ = await Assert.That(driver.Invoker.Created.Single().InteractivePermissions).IsTrue();

        driver.Input.End();
        _ = await running;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Periodic_permission_reconciliation_recovers_a_dropped_event(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced);
        var running = driver.Drive(cancellationToken);
        driver.Invoker.AddPendingPermission(Permission("permission-dropped", requiresReason: false));

        await driver.OutputContains("Allow this write", cancellationToken);
        driver.Input.Type(enhanced ? string.Empty : "allow");
        while (driver.Invoker.PermissionReplies.Count == 0)
        {
            await Task.Delay(5, cancellationToken);
        }

        _ = await Assert.That(driver.Invoker.PendingPermissionLists).IsGreaterThanOrEqualTo(1);
        _ = await Assert.That(driver.Invoker.PermissionReplies.Single().PermissionRequestId)
            .IsEqualTo("permission-dropped");

        driver.Input.End();
        _ = await running;
    }

    [Test]
    public async Task Blank_permission_reason_re_presents_without_replying(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: false);
        var running = driver.Drive(cancellationToken);
        var pending = Permission("permission-reason", requiresReason: true);
        driver.Invoker.AddPendingPermission(pending);
        await driver.Invoker.Publish(new Event { PermissionPending = pending.Clone() });

        await driver.OutputContains("Allow this write", cancellationToken);
        driver.Input.Type("allow");
        driver.Input.Type("   ");
        await driver.ErrorContains("A reason is required.", cancellationToken);
        _ = await Assert.That(driver.Invoker.PermissionReplies).IsEmpty();

        driver.Input.Type("allow");
        driver.Input.Type("  needed for the build  ");
        while (driver.Invoker.PermissionReplies.Count == 0)
        {
            await Task.Delay(5, cancellationToken);
        }

        _ = await Assert.That(driver.Invoker.PermissionReplies.Single().Reason).IsEqualTo("needed for the build");
        driver.Input.End();
        _ = await running;
    }

    [Test]
    public async Task Tool_lifecycle_events_render_as_plain_lines(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event { TextChunk = new TextChunk { Fragment = "checking" } }, cancellationToken);
        await stream.WriteAsync(
            new Event { ToolStarted = new ToolStarted { ToolCallId = "call-1", ToolName = "read" } },
            cancellationToken);
        await stream.WriteAsync(
            new Event
            {
                ToolFinished = new ToolFinished
                {
                    ToolCallId = "call-1",
                    ToolName = "read",
                    Result = "enhanced-only result",
                },
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event { ToolCancelled = new ToolCancelled { ToolCallId = "call-2", ToolName = "write" } },
            cancellationToken);
        await stream.WriteAsync(
            new Event
            {
                ToolError = new ToolError { ToolCallId = "call-3", ToolName = "shell", Message = "denied" },
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event { AgentStarted = new AgentStarted { Name = "explorer" } }, cancellationToken);
        await stream.WriteAsync(
            new Event { AgentFinished = new AgentFinished { Name = "explorer" } }, cancellationToken);
        await stream.WriteAsync(
            new Event { AgentFailed = new AgentFailed { Name = "reviewer", Message = "boom" } }, cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        _ = await BasicCli.RenderTurn(stream.Reader, output, error, cancellationToken);

        _ = await Assert.That(output.ToString()).IsEqualTo(
            $"checking{Environment.NewLine}" +
            $"  tool started: read{Environment.NewLine}" +
            $"  tool finished: read{Environment.NewLine}" +
            $"  tool cancelled: write{Environment.NewLine}" +
            $"  tool error: shell: denied{Environment.NewLine}" +
            $"  agent started: explorer{Environment.NewLine}" +
            $"  agent finished: explorer{Environment.NewLine}" +
            $"  agent failed: reviewer: boom{Environment.NewLine}");
        _ = await Assert.That(output.ToString()).DoesNotContain("enhanced-only result");
        _ = await Assert.That(error.ToString()).IsEmpty();
    }

    [Test]
    public async Task Turn_completion_reports_cumulative_token_totals(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event
            {
                TurnEnded = new TurnEnded { FinishReason = "stop", InputTokens = 1234, OutputTokens = 567 },
            },
            cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        var completed = await BasicCli.RenderTurn(stream.Reader, output, error, cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(output.ToString()).Contains("stop, 1234 total in / 567 total out");
        _ = await Assert.That(error.ToString()).IsEmpty();
    }

    [Test]
    public async Task Length_completion_reports_cumulative_token_totals(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event { TurnEnded = new TurnEnded { FinishReason = "length", InputTokens = 100, OutputTokens = 1 } },
            cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        var completed = await BasicCli.RenderTurn(stream.Reader, output, error, cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(output.ToString()).Contains("length, 100 total in / 1 total out");
        _ = await Assert.That(error.ToString()).IsEmpty();
    }

    [Test]
    public async Task Status_injection_renders_as_its_own_notification(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(new Event { Id = "status", StatusInjected = new StatusInjected() }, cancellationToken);
        await stream.WriteAsync(
            new Event { Id = "ended", TurnEnded = new TurnEnded { FinishReason = "stop" } }, cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        var completed = await BasicCli.RenderTurn(stream.Reader, output, error, cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(output.ToString()).Contains("↻ Status prompt injected");
        _ = await Assert.That(output.ToString()).DoesNotContain("  ↻ Status prompt injected");
        _ = await Assert.That(error.ToString()).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_line_typed_during_a_turn_is_sent_rather_than_held_until_it_ends(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced);

        // A turn that starts and never ends. The old driver blocked in
        // RenderTurn until TurnEnded, so a line typed from here could not be
        // read at all, let alone sent.
        await driver.Invoker.Publish(new Event { Id = "e1", TurnStarted = new TurnStarted { Model = "model" } });

        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("first prompt");
        await driver.Sent(1, cancellationToken);

        driver.Input.Type("typed while working");
        await driver.Sent(2, cancellationToken);

        driver.Input.End();
        _ = await driving;

        _ = await Assert.That(string.Join(" | ", driver.Invoker.Sent))
            .IsEqualTo("first prompt | typed while working");
        _ = await Assert.That(driver.Invoker.Interrupts).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_stale_pending_question_does_not_end_the_cli(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced);
        driver.Invoker.ReplyQuestionNotFound = true;
        driver.Invoker.PendingQuestions.Add(new PendingQuestion
        {
            Id = "question-1",
            Questions =
            {
                new QuestionDefinition
                {
                    Id = "choice-1",
                    Header = "Decision",
                    Prompt = "Choose an approach",
                    Options = { new QuestionOption { Id = "one", Label = "One" } },
                },
            },
        });
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("first prompt");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event { Id = "start", TurnStarted = new TurnStarted { Model = "model" } });
        await driver.Invoker.Publish(
            new Event { Id = "question", ToolStarted = new ToolStarted { ToolCallId = "call-1", ToolName = "question" } });
        await driver.OutputContains("Choose an approach", cancellationToken);

        driver.Input.Type(enhanced ? string.Empty : "one");
        while (driver.Invoker.QuestionReplies.Count < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        driver.Input.Type("second prompt");
        await driver.Sent(2, cancellationToken);
        driver.Input.Type("/exit");
        var exitCode = await driving.WaitAsync(cancellationToken);

        _ = await Assert.That(exitCode).IsEqualTo(CommandDispatcher.ExitSuccess);
        _ = await Assert.That(string.Join(" | ", driver.Invoker.Sent)).IsEqualTo("first prompt | second prompt");
        _ = await Assert.That(driver.Invoker.QuestionReplies).HasSingleItem();
        _ = await Assert.That(driver.Invoker.QuestionReplies[0].QuestionRequestId).IsEqualTo("question-1");
        _ = await Assert.That(driver.Invoker.PendingQuestions).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_queued_turn_is_rendered_without_another_message(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced);
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("first prompt");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event { Id = "start-1", TurnStarted = new TurnStarted { Model = "model" } });

        driver.Input.Type("queued prompt");
        await driver.Sent(2, cancellationToken);
        await driver.Invoker.Publish(new Event { Id = "text-1", TextChunk = new TextChunk { Fragment = "first answer" } });
        await driver.Invoker.Publish(new Event { Id = "end-1", TurnEnded = new TurnEnded { FinishReason = "stop" } });
        await driver.Invoker.Publish(new Event { Id = "start-2", TurnStarted = new TurnStarted { Model = "model" } });
        await driver.Invoker.Publish(new Event { Id = "text-2", TextChunk = new TextChunk { Fragment = "queued answer" } });
        await driver.Invoker.Publish(new Event { Id = "end-2", TurnEnded = new TurnEnded { FinishReason = "stop" } });

        await driver.OutputContains("queued answer", cancellationToken);
        driver.Input.End();
        _ = await driving;

        _ = await Assert.That(driver.Invoker.Sent.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Ctrl_c_stops_the_turn_once_and_then_stops_parrot(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced);

        await driver.Invoker.Publish(new Event { Id = "e1", TurnStarted = new TurnStarted { Model = "model" } });

        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("first prompt");
        await driver.Sent(1, cancellationToken);

        // The first one is the turn's, and the process is left alone.
        driver.Interrupts.Signal();

        while (driver.Invoker.Interrupts < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        _ = await Assert.That(driver.Stopping.IsCancellationRequested).IsFalse();

        // The second is not: the request is still outstanding, so asking again
        // is asking for something else.
        driver.Interrupts.Signal();

        _ = await Assert.That(driver.Stopping.IsCancellationRequested).IsTrue();
        _ = await Assert.That(driver.Invoker.Interrupts).IsEqualTo(1);

        driver.Input.End();
        _ = await driving;
    }

    private static PendingPermission Permission(string id, bool requiresReason)
    {
        var pending = new PendingPermission
        {
            Id = id,
            AgentSessionId = "agent",
            Reason = "Allow this write",
        };
        pending.Targets.Add(new PermissionTarget
        {
            Kind = PermissionTargetKind.File,
            Scope = PermissionTargetScope.Write,
            Path = "/workspace/file.txt",
        });
        pending.Choices.Add(new PermissionChoice
        {
            Value = "allow",
            Label = "Allow",
            Action = PermissionAction.Allow,
            RequiresReason = requiresReason,
        });
        pending.Choices.Add(new PermissionChoice
        {
            Value = "reject",
            Label = "Reject",
            Action = PermissionAction.Deny,
        });
        return pending;
    }
}
