using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class BasicCliTests
{
    [Test]
    public async Task Provider_request_phases_do_not_interrupt_partial_text(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(new Event { TextChunk = new TextChunk { Fragment = "before" } }, cancellationToken);
        foreach (var phase in new[] { ProviderRequestPhase.Requesting, ProviderRequestPhase.HeadersReceived, ProviderRequestPhase.Idle })
        {
            await stream.WriteAsync(
                new Event { ProviderRequestPhaseChanged = new ProviderRequestPhaseChangedEvent { Phase = phase } },
                cancellationToken);
        }

        await stream.WriteAsync(new Event { TextChunk = new TextChunk { Fragment = "after" } }, cancellationToken);
        stream.Complete();
        using var output = new StringWriter();
        using var error = new StringWriter();
        _ = await BasicCli.RenderTurn(stream.Reader, output, error, cancellationToken);
        _ = await Assert.That(output.ToString()).IsEqualTo("beforeafter");
        _ = await Assert.That(error.ToString()).IsEqualTo(string.Empty);
    }

    [Test]
    [Arguments(false, false, "interactive")]
    [Arguments(false, true, "interactive")]
    [Arguments(true, false, "interactive")]
    [Arguments(true, true, "interactive")]
    [Arguments(false, false, "one-shot")]
    [Arguments(false, true, "one-shot")]
    [Arguments(true, false, "one-shot")]
    [Arguments(true, true, "one-shot")]
    [Arguments(false, false, "piped")]
    [Arguments(false, true, "piped")]
    public async Task Acquired_session_is_used_without_creating_or_logging_again(
        bool enhanced,
        bool loaded,
        string inputMode,
        CancellationToken cancellationToken)
    {
        var request = new Parrot.Cli.Enhanced.EnhancedChatRequest(
            new CreateSessionRequest { Model = "ignored/model", Mode = "ignored-mode" },
            inputMode == "one-shot" ? "hello" : string.Empty)
        {
            InitialSession = new UserSession
            {
                Id = "already-open",
                Model = "provider/model",
                Mode = "build",
                Loaded = loaded,
            },
        };
        using var driver = new CliLifecycleDriver(enhanced, request) { InputRedirected = inputMode == "piped" };
        if (inputMode == "piped")
        {
            driver.Input.Type("hello");
            driver.Input.End();
        }

        var running = driver.Drive(cancellationToken);
        if (inputMode == "interactive")
        {
            while (driver.Input.Reads < 1)
            {
                await Task.Delay(5, cancellationToken);
            }

            driver.Input.Type("hello");
        }

        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish("already-open", new Event
        {
            TurnEnded = new TurnEnded { FinishReason = "stop" },
        });
        driver.Input.End();
        _ = await running.WaitAsync(cancellationToken);

        _ = await Assert.That(driver.Invoker.Created).IsEmpty();
        _ = await Assert.That(driver.Invoker.SentTo.Single()).IsEqualTo("already-open");
        _ = await Assert.That(driver.Output).DoesNotContain("Loaded session");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Shutdown_cancels_an_in_progress_summary_without_hiding_output_failures(
        bool failOutput,
        CancellationToken cancellationToken)
    {
        using var output = new CancelledSummaryWriter(failOutput);
        using var driver = new CliLifecycleDriver(false) { OutputWriter = output };
        var running = driver.Drive(cancellationToken);
        driver.Input.Type("hello");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event { TurnEnded = new TurnEnded { FinishReason = "stop" } });
        await output.SummaryStarted.Task.WaitAsync(cancellationToken);

        driver.Input.End();

        if (failOutput)
        {
            _ = await Assert.That(async () => await running.WaitAsync(cancellationToken)).Throws<IOException>();
        }
        else
        {
            _ = await Assert.That(await running.WaitAsync(cancellationToken)).IsEqualTo(CommandDispatcher.ExitSuccess);
        }

        _ = await Assert.That(output.SummaryCancelled).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Empty_piped_input_does_not_create_a_session(bool acquired, CancellationToken cancellationToken)
    {
        var request = new Parrot.Cli.Enhanced.EnhancedChatRequest(new CreateSessionRequest(), string.Empty)
        {
            InitialSession = acquired ? new UserSession { Id = "already-open" } : null,
        };
        using var driver = new CliLifecycleDriver(false, request) { InputRedirected = true };
        driver.Input.End();

        var exitCode = await driver.Drive(cancellationToken);

        _ = await Assert.That(exitCode).IsEqualTo(CommandDispatcher.ExitUsage);
        _ = await Assert.That(driver.Invoker.Created).IsEmpty();
        _ = await Assert.That(driver.Invoker.SentTo).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Model_preset_commands_dispatch_in_both_interactive_clients(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced);
        driver.Invoker.ModelPreset.Name = "Work";
        driver.Invoker.ModelPreset.Model = "high_llm";
        driver.Invoker.SelectedModelPresetModel = "high_llm";
        var running = driver.Drive(cancellationToken);

        while (driver.Input.Reads < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        driver.Input.Type("/model-preset-set Work");
        while (driver.Invoker.SetModelPresets.Count == 0)
        {
            await Task.Delay(5, cancellationToken);
        }

        await driver.OutputContains("Model preset saved: Work = high_llm", cancellationToken);
        driver.Input.End();
        _ = await running.WaitAsync(cancellationToken);
        _ = await Assert.That(driver.Invoker.SetModelPresets.Single().Name).IsEqualTo("Work");

        using var selecting = new CliLifecycleDriver(enhanced);
        selecting.Invoker.ModelPreset.Name = "Work";
        selecting.Invoker.ModelPreset.Model = "high_llm";
        selecting.Invoker.SelectedModelPresetModel = "high_llm";
        var selectingRun = selecting.Drive(cancellationToken);
        while (selecting.Input.Reads < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        selecting.Input.Type("/model-preset-select Work");
        while (selecting.Invoker.SelectedModelPresets.Count == 0)
        {
            await Task.Delay(5, cancellationToken);
        }

        await selecting.OutputContains("Model preset selected: Work = high_llm", cancellationToken);
        selecting.Input.End();
        _ = await selectingRun.WaitAsync(cancellationToken);
        _ = await Assert.That(selecting.Invoker.SelectedModelPresets.Single().Name).IsEqualTo("Work");
    }

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
        await driver.OutputContains("warning: model alias \"z_llm\" is not configured. Use /model-alias to configure.", cancellationToken);
        driver.Input.End();
        _ = await driving;

        var first = driver.Output.IndexOf(
            "warning: model alias \"a_llm\" is not configured. Use /model-alias to configure.", StringComparison.Ordinal);
        var last = driver.Output.IndexOf(
            "warning: model alias \"z_llm\" is not configured. Use /model-alias to configure.", StringComparison.Ordinal);
        _ = await Assert.That(first).IsGreaterThanOrEqualTo(0);
        _ = await Assert.That(last).IsGreaterThan(first);
        _ = await Assert.That(driver.Output).DoesNotContain(
            "warning: model alias \"configured_llm\" is not configured. Use /model-alias to configure.");
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

        var pending = new PermissionFixture("permission-1", requiresReason: false).Pending;
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
        driver.Invoker.AddPendingPermission(new PermissionFixture("permission-dropped", requiresReason: false).Pending);

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
        var pending = new PermissionFixture("permission-reason", requiresReason: true).Pending;
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
            new Event { AgentFinished = new AgentFinished { Name = "explorer", ElapsedMs = 65_000 } }, cancellationToken);
        await stream.WriteAsync(
            new Event { AgentFailed = new AgentFailed { Name = "reviewer", Message = "boom" } }, cancellationToken);
        await stream.WriteAsync(new Event { CompactionStarted = new CompactionStarted() }, cancellationToken);
        await stream.WriteAsync(new Event { CompactionFinished = new CompactionFinished() }, cancellationToken);
        await stream.WriteAsync(
            new Event { CompactionFailed = new CompactionFailed { Message = "boom" } }, cancellationToken);
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
            $"  agent finished: explorer (1m 05s){Environment.NewLine}" +
            $"  agent failed: reviewer: boom{Environment.NewLine}" +
            $"  compaction started{Environment.NewLine}" +
            $"  compaction finished{Environment.NewLine}" +
            $"  compaction failed: boom{Environment.NewLine}");
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
    public async Task Turn_failure_does_not_render_the_enhanced_provider_body(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event
            {
                TurnFailed = new TurnFailed
                {
                    Message = "request failed",
                    ProviderResponseBody = "provider body",
                },
            },
            cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        var completed = await BasicCli.RenderTurn(stream.Reader, output, error, cancellationToken);

        _ = await Assert.That(completed).IsFalse();
        _ = await Assert.That(error.ToString()).Contains("request failed");
        _ = await Assert.That(error.ToString()).DoesNotContain("provider body");
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
    public async Task Active_work_reminder_renders_as_its_own_notification(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event { Id = "text", TextChunk = new TextChunk { Fragment = "draft" } },
            cancellationToken);
        await stream.WriteAsync(
            new Event
            {
                Id = "reminder",
                ActiveWorkReminderInjected = new ActiveWorkReminderInjected(),
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event { Id = "ended", TurnEnded = new TurnEnded { FinishReason = "stop" } }, cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        var completed = await BasicCli.RenderTurn(stream.Reader, output, error, cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(output.ToString()).Contains("draft\n↻ Active work reminder injected");
        _ = await Assert.That(output.ToString()).DoesNotContain("  ↻ Active work reminder injected");
        _ = await Assert.That(error.ToString()).IsEmpty();
    }

    [Test]
    [Arguments("finish the port", "↻ Exit reminder set: port: finish the port")]
    [Arguments(null, "↻ Exit reminder cleared: port")]
    public async Task Exit_reminder_changes_render_as_their_own_notification(
        string? reminder,
        string expected,
        CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event { Id = "text", TextChunk = new TextChunk { Fragment = "draft" } },
            cancellationToken);
        await stream.WriteAsync(
            new Event
            {
                Id = "goal",
                ExitReminderChanged = reminder is null
                    ? new ExitReminderChanged { Title = "port", Cleared = true }
                    : new ExitReminderChanged { Title = "port", Description = reminder },
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event { Id = "ended", TurnEnded = new TurnEnded { FinishReason = "stop" } }, cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        var completed = await BasicCli.RenderTurn(stream.Reader, output, error, cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(output.ToString()).Contains($"draft\n{expected}");
        _ = await Assert.That(output.ToString()).DoesNotContain($"  {expected}");
        _ = await Assert.That(error.ToString()).IsEmpty();
    }

    [Test]
    public async Task Context_reminder_renders_as_its_own_notification(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event { Id = "text", TextChunk = new TextChunk { Fragment = "draft" } },
            cancellationToken);
        await stream.WriteAsync(
            new Event { Id = "reminder", ContextReminderInjected = new ContextReminderInjected { UsagePercent = 27 } },
            cancellationToken);
        await stream.WriteAsync(
            new Event { Id = "ended", TurnEnded = new TurnEnded { FinishReason = "stop" } }, cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        var completed = await BasicCli.RenderTurn(stream.Reader, output, error, cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(output.ToString()).Contains("draft\n↻ Context reminder injected (27% context used)");
        _ = await Assert.That(output.ToString()).DoesNotContain("  ↻ Context reminder injected");
        _ = await Assert.That(error.ToString()).IsEmpty();
    }

    [Test]
    public async Task Provider_request_limit_prompts_render_as_their_own_notifications(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event { Id = "text", TextChunk = new TextChunk { Fragment = "draft" } },
            cancellationToken);
        await stream.WriteAsync(
            new Event
            {
                Id = "final-provider-request",
                FinalProviderRequestPromptInjected = new FinalProviderRequestPromptInjected(),
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event
            {
                Id = "tool-availability-restored",
                ToolAvailabilityRestoredPromptInjected = new ToolAvailabilityRestoredPromptInjected(),
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event
            {
                Id = "skill-loaded",
                SkillLoaded = new SkillLoadedEvent { Path = "/skills/example/SKILL.md" },
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event { Id = "ended", TurnEnded = new TurnEnded { FinishReason = "stop" } }, cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        var completed = await BasicCli.RenderTurn(stream.Reader, output, error, cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(output.ToString()).Contains("draft\n↻ Final provider request prompt injected");
        _ = await Assert.That(output.ToString()).Contains("↻ Tool availability restored prompt injected");
        _ = await Assert.That(output.ToString()).Contains("↻ Skill loaded: /skills/example/SKILL.md");
        _ = await Assert.That(output.ToString()).DoesNotContain("  ↻ Final provider request prompt injected");
        _ = await Assert.That(output.ToString()).DoesNotContain("  ↻ Tool availability restored prompt injected");
        _ = await Assert.That(output.ToString()).DoesNotContain("  ↻ Skill loaded:");
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
        var pending = new PendingQuestion
        {
            Id = "question-1",
            Questions =
            {
                new QuestionDefinition
                {
                    Header = "Decision",
                    Prompt = "Choose an approach",
                    Options = { new Parrot.Protocol.QuestionOption { Label = "One", Description = string.Empty } },
                },
            },
        };
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("first prompt");
        await driver.Sent(1, cancellationToken);
        driver.Invoker.AddPendingQuestion(pending);
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
        _ = await Assert.That(driver.Invoker.QuestionReplies[0].Answers.Single().Text)
            .IsEqualTo(enhanced ? "One" : "one");
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

    [Test]
    public async Task Agent_task_progress_snapshots_append_complete_trees_after_partial_text_and_flush(
        CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event { TextChunk = new TextChunk { Fragment = "partial" } }, cancellationToken);
        await stream.WriteAsync(
            new Event { AgentTaskProgressSnapshot = new ProgressFixture(AgentTaskProgressStatus.Running).Snapshot }, cancellationToken);
        await stream.WriteAsync(
            new Event { AgentTaskProgressSnapshot = new ProgressFixture(AgentTaskProgressStatus.Succeeded).Snapshot }, cancellationToken);
        stream.Complete();
        using var output = new FlushTrackingWriter();
        using var error = new StringWriter();

        _ = await BasicCli.RenderTurn(stream.Reader, output, error, cancellationToken);

        var rendered = output.ToString();
        _ = await Assert.That(rendered).Contains("partial" + Environment.NewLine + "Agent tasks:");
        _ = await Assert.That(Count(rendered, "Agent tasks:")).IsEqualTo(2);
        _ = await Assert.That(rendered).Contains("◐ root[2J    日本");
        _ = await Assert.That(rendered).Contains("├── ○ pending");
        _ = await Assert.That(rendered).Contains("├── ✓ succeeded");
        _ = await Assert.That(rendered).Contains("├── ✗ failed");
        _ = await Assert.That(rendered).Contains("├── ⊘ blocked");
        _ = await Assert.That(rendered).Contains("└── ■ canceled");
        _ = await Assert.That(rendered).DoesNotContain("\u001b[2J");
        _ = await Assert.That(output.Flushes).Count().IsEqualTo(2);
        _ = await Assert.That(output.Flushes[0]).DoesNotContain("✓ root");
        _ = await Assert.That(output.Flushes[1]).Contains("✓ root[2J    日本");
        _ = await Assert.That(error.ToString()).IsEmpty();
    }

    [Test]
    public async Task Plan_report_writes_markdown_then_the_sanitized_pending_task_tree(
        CancellationToken cancellationToken)
    {
        var tree = new AgentTaskProgressSnapshot();
        var root = new AgentTaskProgressNode
        {
            Name = "root\u001b[2J\tnode",
            Status = AgentTaskProgressStatus.Pending,
        };
        var first = new AgentTaskProgressNode { Name = "first", Status = AgentTaskProgressStatus.Pending };
        first.Children.Add(new AgentTaskProgressNode { Name = "nested", Status = AgentTaskProgressStatus.Pending });
        root.Children.Add(first);
        root.Children.Add(new AgentTaskProgressNode { Name = "last", Status = AgentTaskProgressStatus.Pending });
        tree.RootNodes.Add(root);
        using var output = new StringWriter();

        await BasicCli.WritePlanReport(
            new PlanCompleted { Markdown = "# Plan markdown", TaskTree = tree },
            output,
            cancellationToken);

        var rendered = output.ToString();
        _ = await Assert.That(rendered).StartsWith("# Plan markdown" + Environment.NewLine + "Agent tasks:");
        _ = await Assert.That(rendered).Contains("○ root[2J    node");
        _ = await Assert.That(rendered).Contains("├── ○ first");
        _ = await Assert.That(rendered).Contains("│   └── ○ nested");
        _ = await Assert.That(rendered).Contains("└── ○ last");
        _ = await Assert.That(rendered).DoesNotContain("\u001b[2J");
    }

    [Test]
    public async Task Plan_report_without_a_task_tree_writes_only_markdown(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();

        await BasicCli.WritePlanReport(new PlanCompleted { Markdown = "# Plan" }, output, cancellationToken);

        _ = await Assert.That(output.ToString()).IsEqualTo("# Plan" + Environment.NewLine);
    }

    private static int Count(string value, string part)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(part, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += part.Length;
        }

        return count;
    }

    private sealed class ProgressFixture
    {
        public ProgressFixture(AgentTaskProgressStatus rootStatus)
        {
            var snapshot = new AgentTaskProgressSnapshot { OriginToolCallId = "call", Revision = 1 };
            var root = new AgentTaskProgressNode { Name = "root\u001b[2J\t日本", Status = rootStatus };
            root.Children.Add(new AgentTaskProgressNode { Name = "pending", Status = AgentTaskProgressStatus.Pending });
            root.Children.Add(new AgentTaskProgressNode { Name = "succeeded", Status = AgentTaskProgressStatus.Succeeded });
            root.Children.Add(new AgentTaskProgressNode { Name = "failed", Status = AgentTaskProgressStatus.Failed });
            root.Children.Add(new AgentTaskProgressNode { Name = "blocked", Status = AgentTaskProgressStatus.Blocked });
            root.Children.Add(new AgentTaskProgressNode { Name = "canceled", Status = AgentTaskProgressStatus.Canceled });
            snapshot.RootNodes.Add(root);
            Snapshot = snapshot;
        }

        public AgentTaskProgressSnapshot Snapshot { get; }
    }

    private sealed class PermissionFixture
    {
        public PermissionFixture(string id, bool requiresReason)
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
            Pending = pending;
        }

        public PendingPermission Pending { get; }
    }

    private sealed class CancelledSummaryWriter(bool failOutput) : StringWriter
    {
        public TaskCompletionSource SummaryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SummaryCancelled { get; private set; }

        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken)
        {
            if (!buffer.Span.StartsWith("  turn ended", StringComparison.Ordinal))
            {
                await base.WriteLineAsync(buffer, cancellationToken);
                return;
            }

            _ = SummaryStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SummaryCancelled = true;
                if (failOutput)
                {
                    throw new IOException("Summary output failed.");
                }

                throw;
            }
        }
    }

    private sealed class FlushTrackingWriter : StringWriter
    {
        internal List<string> Flushes { get; } = [];

        public override Task FlushAsync()
        {
            Flushes.Add(ToString());
            return Task.CompletedTask;
        }
    }
}
