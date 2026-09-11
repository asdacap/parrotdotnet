using System.Collections.Concurrent;
using System.Threading.Channels;
using Parrot.Agent;
using Parrot.Auth;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Config;
using Parrot.Protocol;
using Parrot.Tools;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedCliTests
{
    [Test]
    public async Task Main_prompt_submits_after_one_hundred_milliseconds_of_quiet(
        CancellationToken cancellationToken)
    {
        var delay = new ControlledSubmitDelay();
        using var driver = new CliLifecycleDriver(
            true,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            delay.Wait);
        var running = driver.Drive(cancellationToken);

        driver.Input.Type("hello");
        var pending = await delay.Read(cancellationToken);

        _ = await Assert.That(pending.Interval).IsEqualTo(TimeSpan.FromMilliseconds(100));
        _ = await Assert.That(driver.Invoker.Sent).IsEmpty();

        pending.Release();
        await driver.Sent(1, cancellationToken);
        driver.Input.End();
        _ = await running;

        _ = await Assert.That(driver.Invoker.Sent).HasSingleItem();
        _ = await Assert.That(driver.Invoker.Sent[0]).IsEqualTo("hello");
    }

    [Test]
    public async Task Fast_lines_are_combined_before_the_quiet_submit(CancellationToken cancellationToken)
    {
        var delay = new ControlledSubmitDelay();
        using var driver = new CliLifecycleDriver(
            true,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            delay.Wait);
        var running = driver.Drive(cancellationToken);

        driver.Input.Type("first");
        var first = await delay.Read(cancellationToken);
        driver.Input.Type("second");
        var second = await delay.Read(cancellationToken);

        await first.Canceled.WaitAsync(cancellationToken);
        _ = await Assert.That(driver.Invoker.Sent).IsEmpty();

        second.Release();
        await driver.Sent(1, cancellationToken);
        driver.Input.End();
        _ = await running;

        _ = await Assert.That(driver.Invoker.Sent).HasSingleItem();
        _ = await Assert.That(driver.Invoker.Sent[0]).IsEqualTo("first\nsecond");
    }

    [Test]
    public async Task Repeated_fast_enter_preserves_an_interior_blank_line(CancellationToken cancellationToken)
    {
        var delay = new ControlledSubmitDelay();
        using var driver = new CliLifecycleDriver(
            true,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            delay.Wait);
        var running = driver.Drive(cancellationToken);

        driver.Input.Type("first");
        var first = await delay.Read(cancellationToken);
        driver.Input.Type(string.Empty);
        var blank = await delay.Read(cancellationToken);
        driver.Input.Type("second");
        var second = await delay.Read(cancellationToken);

        await first.Canceled.WaitAsync(cancellationToken);
        await blank.Canceled.WaitAsync(cancellationToken);
        second.Release();
        await driver.Sent(1, cancellationToken);
        driver.Input.End();
        _ = await running;

        _ = await Assert.That(driver.Invoker.Sent[0]).IsEqualTo("first\n\nsecond");
    }

    [Test]
    [Arguments("\u001b")]
    [Arguments("\u0003")]
    public async Task Interrupt_keys_preserve_draft_input(string key, CancellationToken cancellationToken)
    {
        using var terminal = new ScriptedTerminal(80);
        using var stopping = new CancellationTokenSource();
        using var http = new HttpClient();
        var invoker = new ScriptedInvoker();
        using var loadedConfiguration = new LoadedConfiguration(string.Empty);
        var configuration = loadedConfiguration.Value;
        var presenters = new ToolPresenterRegistry([], new GenericToolPresenter());
        var renderer = new EnhancedTurnRenderer(terminal, configuration, presenters);
        using var diagnostics = new TransportDiagnosticsFixture();
        var cli = new EnhancedCli(
            new GeneratedParrot.ParrotClient(invoker),
            new Interrupts(stopping),
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            new UnusedCredentials(),
            new OpenAiOAuthClient(http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            configuration,
            ["provider"],
            terminal,
            presenters,
            renderer,
            TimeProvider.System,
            ImmediateDelay(),
            new AttachmentsFixture().Uploader,
            diagnostics.Log);
        var running = cli.Run(cancellationToken);

        terminal.Type("first prompt\r");
        await Sent(invoker, 1, cancellationToken);
        terminal.Type("draft prompt");
        terminal.Type(key);
        terminal.Tick();

        while (invoker.Interrupts < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        _ = await Assert.That(stopping.IsCancellationRequested).IsFalse();
        terminal.Type("\r");
        await Sent(invoker, 2, cancellationToken);
        terminal.End();
        _ = await running;

        _ = await Assert.That(invoker.Interrupts).IsEqualTo(1);
        _ = await Assert.That(string.Join('|', invoker.Sent)).IsEqualTo("first prompt|draft prompt");
    }

    [Test]
    public async Task Up_on_an_empty_prompt_recalls_the_previous_prompt_and_down_discards_it(CancellationToken cancellationToken)
    {
        var delay = new ControlledSubmitDelay();
        using var terminal = new ScriptedTerminal(80);
        using var stopping = new CancellationTokenSource();
        using var http = new HttpClient();
        var invoker = new ScriptedInvoker();
        using var loadedConfiguration = new LoadedConfiguration(string.Empty);
        var configuration = loadedConfiguration.Value;
        var presenters = new ToolPresenterRegistry([], new GenericToolPresenter());
        var renderer = new EnhancedTurnRenderer(terminal, configuration, presenters);
        using var diagnostics = new TransportDiagnosticsFixture();
        var cli = new EnhancedCli(
            new GeneratedParrot.ParrotClient(invoker),
            new Interrupts(stopping),
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            new UnusedCredentials(),
            new OpenAiOAuthClient(http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            configuration,
            ["provider"],
            terminal,
            presenters,
            renderer,
            TimeProvider.System,
            delay.Wait,
            new AttachmentsFixture().Uploader,
            diagnostics.Log);
        var running = cli.Run(cancellationToken);

        terminal.Type("first prompt\r");
        (await delay.Read(cancellationToken)).Release();
        await Sent(invoker, 1, cancellationToken);
        terminal.Type("second prompt\r");
        (await delay.Read(cancellationToken)).Release();
        await Sent(invoker, 2, cancellationToken);

        terminal.Type("\u001b[A");
        await Task.Delay(50, cancellationToken);
        _ = await Assert.That(invoker.Sent.Count).IsEqualTo(2);

        terminal.Type("\u001b[B");
        await Task.Delay(50, cancellationToken);
        terminal.Type("\u001b[A");
        await Task.Delay(50, cancellationToken);

        terminal.Type("\r");
        (await delay.Read(cancellationToken)).Release();
        await Sent(invoker, 3, cancellationToken);
        _ = await Assert.That(invoker.Sent[2]).IsEqualTo("second prompt");

        terminal.End();
        _ = await running;
    }

    [Test]
    public async Task Plan_completion_renders_markdown_and_approves_with_picker(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var running = driver.Drive(cancellationToken);
        driver.Input.Type("draft plan");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            TurnStarted = new TurnStarted { Model = "model" },
        });
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            PlanCompleted = new PlanCompleted
            {
                Markdown = "# Written plan\n\n- change code",
                Dialog = new TurnCompleteDialog
                {
                    Prompt = "Plan complete: ",
                    CustomChoice = "feedback",
                    CustomPrompt = "Feedback: ",
                    CustomDescription = "Revise the plan",
                    Choices =
                    {
                        new DialogChoice
                        {
                            Value = "yes",
                            Description = "Implement it",
                            Action = new ChoiceAction { Mode = "build", Prompt = "Implement the approved plan." },
                        },
                        new DialogChoice { Value = "no", Description = "Keep planning" },
                    },
                },
            },
        });
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            TurnEnded = new TurnEnded { FinishReason = "stop" },
        });

        await driver.OutputContains("Written plan", cancellationToken);
        await driver.OutputContains("Plan complete:", cancellationToken);
        _ = await Assert.That(driver.Output).DoesNotContain("Agent tasks:");
        var outputBeforeApproval = driver.Output.Length;
        driver.Input.Type("yes");
        await driver.Sent(2, cancellationToken);
        await driver.OutputContainsAfter(outputBeforeApproval, "\u001b[2K❯ ", cancellationToken);

        _ = await Assert.That(driver.Invoker.Sent.Count).IsEqualTo(2);
        _ = await Assert.That(driver.Invoker.Sent[0]).IsEqualTo("draft plan");
        _ = await Assert.That(driver.Invoker.Sent[1]).IsEqualTo("Implement the approved plan.");
        _ = await Assert.That(driver.Invoker.Updated).Count().IsEqualTo(1);
        _ = await Assert.That(driver.Invoker.Updated[0].Mode).IsEqualTo("build");

        driver.Input.End();
        _ = await running;
    }

    [Test]
    public async Task Plan_completion_renders_task_tree_after_markdown_and_before_picker(
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var running = driver.Drive(cancellationToken);
        driver.Input.Type("draft plan");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            TurnStarted = new TurnStarted { Model = "model" },
        });
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            PlanCompleted = new PlanCompleted
            {
                Markdown = "# Written plan",
                TaskTree = new AgentTaskProgressSnapshot
                {
                    OriginToolCallId = "plan",
                    Revision = 1,
                    RootNodes =
                    {
                        new AgentTaskProgressNode
                        {
                            Name = "first root",
                            Status = AgentTaskProgressStatus.Pending,
                            Children =
                            {
                                new AgentTaskProgressNode { Name = "nested task", Status = AgentTaskProgressStatus.Pending },
                            },
                        },
                        new AgentTaskProgressNode { Name = "second root", Status = AgentTaskProgressStatus.Pending },
                    },
                },
                Dialog = new TurnCompleteDialog
                {
                    Prompt = "Plan complete: ",
                    Choices =
                    {
                        new DialogChoice
                        {
                            Value = "yes",
                            Description = "Implement it",
                            Action = new ChoiceAction { Mode = "build", Prompt = "Implement the approved plan." },
                        },
                    },
                },
            },
        });
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            TurnEnded = new TurnEnded { FinishReason = "stop" },
        });

        await driver.OutputContains("Plan complete:", cancellationToken);
        var output = driver.Output;
        var markdown = output.IndexOf("Written plan", StringComparison.Ordinal);
        var tree = output.IndexOf("Agent tasks:", StringComparison.Ordinal);
        var firstRoot = output.IndexOf("○ first root", StringComparison.Ordinal);
        var child = output.IndexOf("└── ○ nested task", StringComparison.Ordinal);
        var secondRoot = output.IndexOf("○ second root", StringComparison.Ordinal);
        var picker = output.IndexOf("Plan complete:", StringComparison.Ordinal);
        _ = await Assert.That(markdown).IsGreaterThanOrEqualTo(0);
        _ = await Assert.That(tree).IsGreaterThan(markdown);
        _ = await Assert.That(firstRoot).IsGreaterThan(tree);
        _ = await Assert.That(child).IsGreaterThan(firstRoot);
        _ = await Assert.That(secondRoot).IsGreaterThan(child);
        _ = await Assert.That(picker).IsGreaterThan(secondRoot);

        driver.Input.Type("yes");
        await driver.Sent(2, cancellationToken);
        _ = await Assert.That(driver.Invoker.Updated.Single().Mode).IsEqualTo("build");
        _ = await Assert.That(driver.Invoker.Sent[1]).IsEqualTo("Implement the approved plan.");

        driver.Input.End();
        _ = await running;
    }

    [Test]
    public async Task Initial_plan_turn_shows_the_completion_dialog(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(
            true,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "plan" }, "draft plan"));
        var running = driver.Drive(cancellationToken);
        await driver.Sent(1, cancellationToken);
        _ = await Assert.That(driver.Invoker.Created.Single().InteractivePermissions).IsFalse();
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            TurnStarted = new TurnStarted { Model = "model" },
        });
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            PlanCompleted = new PlanCompleted
            {
                Markdown = "# Written plan\n\n- change code",
                Dialog = new TurnCompleteDialog
                {
                    Prompt = "Plan complete: ",
                    Choices =
                    {
                        new DialogChoice
                        {
                            Value = "yes",
                            Description = "Implement it",
                            Action = new ChoiceAction { Mode = "build", Prompt = "Implement the approved plan." },
                        },
                    },
                },
            },
        });
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            TurnEnded = new TurnEnded { FinishReason = "stop" },
        });

        await driver.OutputContains("Written plan", cancellationToken);
        await driver.OutputContains("Plan complete:", cancellationToken);
        driver.Input.Type("yes");
        await driver.Sent(2, cancellationToken);

        _ = await Assert.That(driver.Invoker.Updated).Count().IsEqualTo(1);
        _ = await Assert.That(driver.Invoker.Updated[0].Mode).IsEqualTo("build");
        _ = await Assert.That(driver.Invoker.Sent[1]).IsEqualTo("Implement the approved plan.");

        driver.Input.End();
        _ = await running;
    }

    [Test]
    public async Task Question_arrival_wakes_input_loop_and_renders_picker(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var running = driver.Drive(cancellationToken);
        driver.Input.Type("ask me");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            TurnStarted = new TurnStarted { Model = "model" },
        });
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            ToolStarted = new ToolStarted { ToolCallId = "call-question", ToolName = "question" },
        });

        while (driver.Invoker.PendingQuestionLists < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        var pending = new PendingQuestion { Id = "question-request" };
        pending.Questions.Add(new QuestionDefinition
        {
            Header = "Question",
            Prompt = "Pick a colour",
            Options = { "Blue" },
        });
        driver.Invoker.AddPendingQuestion(pending);

        await driver.OutputContains("Pick a colour", cancellationToken);
        driver.Input.Type("blue");
        while (driver.Invoker.QuestionReplies.Count < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        var reply = driver.Invoker.QuestionReplies.Single();
        _ = await Assert.That(reply.QuestionRequestId).IsEqualTo("question-request");
        _ = await Assert.That(reply.Answers.Single().Text).IsEqualTo("Blue");

        driver.Input.End();
        _ = await running;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Multiple_choice_question_replies_with_selected_labels_in_option_order(
        bool timed, CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var running = driver.Drive(cancellationToken);
        driver.Input.Type("ask me");
        await driver.Sent(1, cancellationToken);
        await PublishQuestionStart(driver);
        await WaitForQuestionList(driver, 1, cancellationToken);

        var pending = new PendingQuestion { Id = "question-request" };
        pending.Questions.Add(new QuestionDefinition
        {
            Header = "Question",
            Prompt = "Pick colours",
            Options = { "One", "Two", "Three" },
            Multiple = true,
        });
        if (timed)
        {
            pending.RemainingTimeoutMs = 3000;
        }

        driver.Invoker.AddPendingQuestion(pending);

        await driver.OutputContains("Pick colours", cancellationToken);
        var beforeSecondPicker = driver.Output.Length;
        driver.Input.Type(string.Empty);
        await driver.OutputContainsAfter(beforeSecondPicker, "Two", cancellationToken);
        if (timed)
        {
            driver.Invoker.UpdateQuestionTimeout(pending.Id, 1000);
            await driver.OutputContainsAfter(beforeSecondPicker, "Auto-return in 0:01", cancellationToken);
        }

        var beforeDonePicker = driver.Output.Length;
        driver.Input.Type(string.Empty);
        await driver.OutputContainsAfter(beforeDonePicker, "Done", cancellationToken);
        driver.Input.Type("\u001b[B");
        while (driver.Invoker.QuestionReplies.Count < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        var reply = driver.Invoker.QuestionReplies.Single();
        _ = await Assert.That(reply.QuestionRequestId).IsEqualTo("question-request");
        _ = await Assert.That(reply.Answers.Single().Text).IsEqualTo("One, Two");

        driver.Input.End();
        _ = await running;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Question_countdown_updates_while_idle_preserves_answers_and_clears(
        bool custom, CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var running = driver.Drive(cancellationToken);
        driver.Input.Type("ask twice");
        await driver.Sent(1, cancellationToken);
        await PublishQuestionStart(driver);
        await WaitForQuestionList(driver, 1, cancellationToken);
        var pending = new QuestionFixture("question-request", "First choice").Pending;
        pending.RemainingTimeoutMs = 65000;
        pending.Questions.Add(new QuestionDefinition
        {
            Header = "Question",
            Prompt = "Second choice",
            Options = { "Two" },
            Custom = custom,
        });
        driver.Invoker.AddPendingQuestion(pending);
        await driver.OutputContains("Auto-return in 1:05", cancellationToken);
        driver.Input.Type(string.Empty);
        await driver.OutputContains("Second choice", cancellationToken);
        if (custom)
        {
            driver.Input.Type("Custom");
            await driver.OutputContains("Custom answer", cancellationToken);
        }

        var beforeUpdate = driver.Output.Length;
        driver.Invoker.UpdateQuestionTimeout(pending.Id, 0);
        await driver.OutputContainsAfter(beforeUpdate, "Auto-return in 0:00", cancellationToken);
        _ = await Assert.That(driver.Invoker.QuestionReplies).IsEmpty();
        _ = await Assert.That(driver.Invoker.QuestionRejections).IsEmpty();
        driver.Input.Type(custom ? "typed answer" : string.Empty);
        while (driver.Invoker.QuestionReplies.Count == 0)
        {
            await Task.Delay(1, cancellationToken);
        }

        var answers = driver.Invoker.QuestionReplies.Single().Answers;
        _ = await Assert.That(answers.Count).IsEqualTo(2);
        _ = await Assert.That(answers[0].Text).IsEqualTo("One");
        _ = await Assert.That(answers[1].Text).IsEqualTo(custom ? "typed answer" : "Two");
        var afterReply = driver.Output.Length;
        driver.Input.Type("after question");
        await driver.Sent(2, cancellationToken);
        _ = await Assert.That(driver.Output[afterReply..]).DoesNotContain("Auto-return in");
        driver.Input.End();
        _ = await running;
    }

    [Test]
    public async Task Queued_question_uses_fresh_countdown_instead_of_discovery_snapshot(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        driver.Invoker.AddPendingQuestion(new QuestionFixture("question-a", "Active choice").Pending);
        var queued = new QuestionFixture("question-b", "Queued choice").Pending;
        queued.RemainingTimeoutMs = 90000;
        driver.Invoker.AddPendingQuestion(queued);
        var running = driver.Drive(cancellationToken);
        driver.Input.Type("ask twice");
        await driver.Sent(1, cancellationToken);
        await PublishQuestionStart(driver);
        await driver.OutputContains("Active choice", cancellationToken);
        driver.Invoker.UpdateQuestionTimeout(queued.Id, 4000);
        driver.Invoker.RemovePendingQuestion("question-a");
        await driver.OutputContains("Queued choice", cancellationToken);
        await driver.OutputContains("Auto-return in 0:04", cancellationToken);
        _ = await Assert.That(driver.Output).DoesNotContain("Auto-return in 1:30");
        driver.Invoker.RemovePendingQuestion(queued.Id);
        var afterSettlement = driver.Output.Length;
        await driver.OutputContainsAfter(afterSettlement, "\u001b[2K❯ ", cancellationToken);
        driver.Input.End();
        _ = await running;
    }

    [Test]
    public async Task Settled_question_closes_picker_without_local_answer(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var running = driver.Drive(cancellationToken);
        driver.Input.Type("ask me");
        await driver.Sent(1, cancellationToken);
        await PublishQuestionStart(driver);
        await WaitForQuestionList(driver, 1, cancellationToken);
        driver.Invoker.AddPendingQuestion(new QuestionFixture("question-request", "Pick a colour").Pending);

        await driver.OutputContains("Pick a colour", cancellationToken);
        var listedBeforeSettlement = driver.Invoker.PendingQuestionLists;
        var outputBeforeSettlement = driver.Output.Length;
        driver.Invoker.RemovePendingQuestion("question-request");

        await WaitForQuestionList(driver, listedBeforeSettlement + 1, cancellationToken);
        await driver.OutputContainsAfter(outputBeforeSettlement, "\u001b[2K❯ ", cancellationToken);
        driver.Input.Type("after question");
        await driver.Sent(2, cancellationToken);

        _ = await Assert.That(driver.Invoker.QuestionReplies).IsEmpty();
        _ = await Assert.That(driver.Invoker.QuestionRejections).IsEmpty();
        _ = await Assert.That(driver.Invoker.Sent[1]).IsEqualTo("after question");

        driver.Input.End();
        _ = await running;
    }

    [Test]
    public async Task Settled_multi_part_question_discards_collected_answers(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var running = driver.Drive(cancellationToken);
        driver.Input.Type("ask twice");
        await driver.Sent(1, cancellationToken);
        await PublishQuestionStart(driver);
        await WaitForQuestionList(driver, 1, cancellationToken);
        var pending = new QuestionFixture("question-request", "First choice").Pending;
        pending.Questions.Add(new QuestionDefinition
        {
            Header = "Question",
            Prompt = "Second choice",
            Options = { "Two" },
        });
        driver.Invoker.AddPendingQuestion(pending);

        await driver.OutputContains("First choice", cancellationToken);
        driver.Input.Type(string.Empty);
        await driver.OutputContains("Second choice", cancellationToken);
        driver.Invoker.RemovePendingQuestion("question-request");
        var outputBeforeSettlement = driver.Output.Length;

        await driver.OutputContainsAfter(outputBeforeSettlement, "\u001b[2K❯ ", cancellationToken);
        _ = await Assert.That(driver.Invoker.QuestionReplies).IsEmpty();
        _ = await Assert.That(driver.Invoker.QuestionRejections).IsEmpty();

        driver.Input.End();
        _ = await running;
    }

    [Test]
    public async Task Settling_another_request_does_not_close_the_active_question(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var running = driver.Drive(cancellationToken);
        driver.Invoker.AddPendingQuestion(new QuestionFixture("question-a", "Active question").Pending);
        driver.Invoker.AddPendingQuestion(new QuestionFixture("question-b", "Queued question").Pending);
        driver.Input.Type("ask me");
        await driver.Sent(1, cancellationToken);
        await PublishQuestionStart(driver);

        await driver.OutputContains("Active question", cancellationToken);
        var listedBeforeSettlement = driver.Invoker.PendingQuestionLists;
        driver.Invoker.RemovePendingQuestion("question-b");
        await WaitForQuestionList(driver, listedBeforeSettlement + 1, cancellationToken);
        var outputBeforeQueuedQuestion = driver.Output.Length;
        driver.Input.Type(string.Empty);
        while (driver.Invoker.QuestionReplies.Count < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        await driver.OutputContainsAfter(outputBeforeQueuedQuestion, "\u001b[2K❯ ", cancellationToken);
        _ = await Assert.That(driver.Invoker.QuestionReplies).HasSingleItem();
        _ = await Assert.That(driver.Invoker.QuestionReplies[0].QuestionRequestId).IsEqualTo("question-a");
        _ = await Assert.That(driver.Invoker.QuestionRejections).IsEmpty();

        driver.Input.End();
        _ = await running;
    }

    [Test]
    public async Task User_cancellation_rejects_a_pending_question(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var running = driver.Drive(cancellationToken);
        driver.Input.Type("ask me");
        await driver.Sent(1, cancellationToken);
        await PublishQuestionStart(driver);
        await WaitForQuestionList(driver, 1, cancellationToken);
        driver.Invoker.AddPendingQuestion(new QuestionFixture("question-request", "Pick a colour").Pending);

        await driver.OutputContains("Pick a colour", cancellationToken);
        driver.Input.Type("\u001b");
        while (driver.Invoker.QuestionRejections.Count < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        _ = await Assert.That(driver.Invoker.QuestionRejections).HasSingleItem();
        _ = await Assert.That(driver.Invoker.QuestionRejections[0].QuestionRequestId).IsEqualTo("question-request");
        _ = await Assert.That(driver.Invoker.QuestionReplies).IsEmpty();

        driver.Input.End();
        _ = await running;
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task Custom_question_preserves_context_and_optionless_answers_continue_in_order(
        bool multiple, bool optionless, CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var running = driver.Drive(cancellationToken);
        driver.Input.Type("ask me");
        await driver.Sent(1, cancellationToken);
        await PublishQuestionStart(driver);
        await WaitForQuestionList(driver, 1, cancellationToken);
        var pending = new PendingQuestion { Id = "question-request" };
        var question = new QuestionDefinition
        {
            Header = "Answer context",
            Prompt = "Explain your choice",
            Custom = true,
            Multiple = multiple,
        };
        if (!optionless)
        {
            question.Options.Add("Blue");
        }

        pending.Questions.Add(question);
        pending.Questions.Add(new QuestionDefinition { Prompt = "Next answer", Custom = true });
        var beforeQuestion = driver.Output.Length;
        driver.Invoker.AddPendingQuestion(pending);
        await driver.OutputContains("Explain your choice", cancellationToken);
        if (!optionless)
        {
            driver.Input.Type("Custom");
            await driver.OutputContainsAfter(beforeQuestion, "> Custom answer", cancellationToken);
        }

        var beforeRefresh = driver.Output.Length;
        driver.Resize(79);
        await driver.OutputContainsAfter(beforeRefresh, "Explain your choice", cancellationToken);
        var activeOutput = driver.Output[beforeRefresh..];
        _ = await Assert.That(activeOutput).Contains("Answer context");
        _ = await Assert.That(activeOutput).DoesNotContain("Answer context Explain your choice");
        _ = await Assert.That(activeOutput).DoesNotContain("Write an answer");
        driver.Input.Type("  typed answer  ");
        if (multiple && !optionless)
        {
            await driver.OutputContains("Finish selecting", cancellationToken);
            driver.Input.Type("Done");
        }

        await driver.OutputContains("Next answer", cancellationToken);
        driver.Input.Type("second answer");
        while (driver.Invoker.QuestionReplies.Count == 0)
        {
            await Task.Delay(5, cancellationToken);
        }

        var answers = driver.Invoker.QuestionReplies.Single().Answers;
        _ = await Assert.That(answers.Count).IsEqualTo(2);
        _ = await Assert.That(answers[0].Text).IsEqualTo("typed answer");
        _ = await Assert.That(answers[1].Text).IsEqualTo("second answer");
        _ = await Assert.That(driver.Invoker.QuestionRejections).IsEmpty();
        driver.Input.End();
        _ = await running;
    }

    [Test]
    [Arguments(false, "")]
    [Arguments(true, "\u001b")]
    [Arguments(false, "settle")]
    [Arguments(true, "settle")]
    public async Task Optionless_question_rejection_and_settlement_do_not_submit_answers(
        bool multiple, string action, CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var running = driver.Drive(cancellationToken);
        driver.Input.Type("ask me");
        await driver.Sent(1, cancellationToken);
        await PublishQuestionStart(driver);
        await WaitForQuestionList(driver, 1, cancellationToken);
        var pending = new PendingQuestion { Id = "question-request" };
        pending.Questions.Add(new QuestionDefinition
        {
            Prompt = "Write directly",
            Custom = true,
            Multiple = multiple,
        });
        driver.Invoker.AddPendingQuestion(pending);
        await driver.OutputContains("Write directly", cancellationToken);
        var beforeClosure = driver.Output.Length;
        if (action == "settle")
        {
            driver.Invoker.RemovePendingQuestion(pending.Id);
            await driver.OutputContainsAfter(beforeClosure, "\u001b[2K❯ ", cancellationToken);
            _ = await Assert.That(driver.Invoker.QuestionRejections).IsEmpty();
        }
        else
        {
            driver.Input.Type(action);
            while (driver.Invoker.QuestionRejections.Count == 0)
            {
                await Task.Delay(5, cancellationToken);
            }

            _ = await Assert.That(driver.Invoker.QuestionRejections).HasSingleItem();
        }

        _ = await Assert.That(driver.Invoker.QuestionReplies).IsEmpty();
        driver.Input.End();
        _ = await running;
    }

    [Test]
    public async Task Finished_shell_tool_flushes_its_command_to_scrollback(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 80, new TerminalPalette(false), 10, 12, true);
        var fixedItems = new ILiveBufferItem[]
        {
            new ModelineValue("build", "working", "provider/model"),
            new PromptValue("> ", string.Empty, 0),
        };
        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token) =>
            renderer.Draw([.. items, .. fixedItems], token);
        Task Commit(
            IScrollbackItem scrollback,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken token) => renderer.Commit(scrollback, [.. items, .. fixedItems], token);
        await using var view = new RawActivityView(
            Draw,
            Commit,
            new ToolPresenterRegistry([new ExecCommandToolPresenter(TimeProvider.System, [])], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);

        await view.Render(
            new Event
            {
                AgentSessionId = "main-session",
                TurnStarted = new TurnStarted { Model = "model" },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "main-session",
                ToolCallChunk = new ToolCallChunk
                {
                    ToolCallId = "call-1",
                    ToolName = "exec_command",
                    ArgumentsFragment = "{\"command\":\"dotnet test\"}",
                },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "main-session",
                ToolFinished = new ToolFinished
                {
                    ToolCallId = "call-1",
                    ToolName = "exec_command",
                    Result = "Process exited with code 0 after 1.23s\nall tests passed",
                },
            },
            cancellationToken);

        var rendered = output.ToString();
        _ = await Assert.That(rendered).Contains("✓ $ dotnet test\r\n");
        _ = await Assert.That(rendered).Contains("Process exited with code 0 after 1.23s\r\nall tests passed\r\n");
        _ = await Assert.That(rendered).DoesNotContain("exec_command finished");
    }

    [Test]
    public async Task Live_status_tracks_concurrent_agent_turns_and_owner_qualified_tools(
        CancellationToken cancellationToken)
    {
        var draws = new ConcurrentQueue<string>();
        var committed = new ConcurrentQueue<string>();
        var ticks = Channel.CreateUnbounded<bool>();
        var context = new LiveBufferRenderContext(120, new TerminalPalette(false));

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            draws.Enqueue(RenderItems(items));
            return Task.CompletedTask;
        }

        Task Commit(
            IScrollbackItem scrollback,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            committed.Enqueue(string.Join('|', scrollback.Render(new ScrollbackRenderContext(120, context.Palette))));
            draws.Enqueue(RenderItems(items));
            return Task.CompletedTask;
        }

        async Task Delay(CancellationToken token) =>
            _ = await ticks.Reader.ReadAsync(token);

        string RenderItems(IReadOnlyList<ILiveBufferItem> items) =>
            string.Join('|', items.SelectMany(item => item.Render(context).Lines).Select(line => line.Text));

        var mainActivities = new List<string>();
        Task UpdateMainAgentActivity(string activity, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            mainActivities.Add(activity);
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(Draw, Commit, Delay, new ToolPresenterRegistry([], new GenericToolPresenter()), UpdateMainAgentActivity);
        using var animating = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var animation = view.Run(animating.Token);

        await view.Render(
            new Event { AgentSessionId = "stale-session", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "main-session", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "main-session",
                AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
                {
                    InputTokens = 2_400,
                    CachedInputTokens = 1_600,
                    OutputTokens = 600,
                    ContextSize = 3_000,
                    ContextLimit = 128_000,
                },
            },
            cancellationToken);
        _ = await Assert.That(mainActivities[^1]).IsEqualTo("agent main");
        _ = await Assert.That(mainActivities[^1]).DoesNotContain("in / ");
        _ = await Assert.That(mainActivities[^1]).DoesNotContain("ctx");
        await view.Render(
            new Event
            {
                AgentSessionId = "child-session",
                AgentStarted = new AgentStarted
                {
                    ParentAgentSessionId = "main-session",
                    Name = "explorer\u001b[31m",
                },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child-session", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child-session",
                AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
                {
                    InputTokens = 1200000,
                    CachedInputTokens = 800,
                    OutputTokens = 300,
                    ContextSize = 1500,
                    ContextLimit = 0,
                },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "main-session",
                ToolCallChunk = new ToolCallChunk
                {
                    ToolCallId = "shared-call",
                    ToolName = "exec_command",
                    ArgumentsFragment = "dotnet test",
                },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "main-session",
                ToolStarted = new ToolStarted { ToolCallId = "shared-call", ToolName = "exec_command" },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child-session",
                ToolStarted = new ToolStarted { ToolCallId = "shared-call", ToolName = "read\u001b[2J" },
            },
            cancellationToken);
        await view.ReplaceContent([new LiveTextValue("answer")], cancellationToken);

        var live = draws.Last();
        _ = await Assert.That(live).Contains("answer");
        _ = await Assert.That(live).DoesNotContain("agent main");
        _ = await Assert.That(mainActivities).Contains("agent main");
        _ = await Assert.That(live).Contains("  ⠋ [explorer[31m] agent explorer[31m (1.2m in / 800 cached / 300 out, 1.5k/? ctx)");
        _ = await Assert.That(live).Contains("⠋ exec_command");
        _ = await Assert.That(live).Contains("  ⠋ [explorer[31m] read[2J");
        _ = await Assert.That(live).DoesNotContain("|⠋ [explorer[31m] read[2J");

        await ticks.Writer.WriteAsync(true, cancellationToken);
        while (draws.Count < 2)
        {
            await Task.Delay(1, cancellationToken);
        }

        await view.Render(
            new Event
            {
                AgentSessionId = "main-session",
                ToolFinished = new ToolFinished { ToolCallId = "shared-call", ToolName = "exec_command" },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child-session",
                ToolError = new ToolError
                {
                    ToolCallId = "shared-call",
                    ToolName = "read",
                    Message = "denied\u001b[2J",
                },
            },
            cancellationToken);
        await view.CommitContent(ImmediateScrollbackValue.Muted(["answer"]), [], cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "main-session", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        _ = await Assert.That(mainActivities.Last()).IsEmpty();
        await view.ReplaceContent([], cancellationToken);
        _ = await Assert.That(draws.Last()).Contains("agent explorer[31m");
        _ = await Assert.That(draws.Last()).DoesNotContain("agent main");

        await view.Render(
            new Event { AgentSessionId = "child-session", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        var beforeAgentFinished = committed.Count;
        await view.Render(
            new Event
            {
                AgentSessionId = "child-session",
                AgentFinished = new AgentFinished
                {
                    ParentAgentSessionId = "main-session",
                    Name = "explorer",
                    ElapsedMs = 7_000,
                },
            },
            cancellationToken);

        await animating.CancelAsync();
        await animation;

        _ = await Assert.That(beforeAgentFinished).IsEqualTo(4);
        _ = await Assert.That(committed.Count).IsEqualTo(5);
        _ = await Assert.That(string.Join('|', committed)).Contains("✓ tool call exec_command|dotnet test");
        _ = await Assert.That(string.Join('|', committed)).Contains("  ✗ [explorer[31m] tool call read[2J|    [explorer[31m] denied[2J");
        _ = await Assert.That(string.Join('|', committed)).Contains("  ♟ [explorer] agent finished (7s)");
        _ = await Assert.That(draws.Last()).IsEmpty();
    }

    [Test]
    public async Task Turn_view_events_replace_cached_content_without_drawing_the_terminal(
        CancellationToken cancellationToken)
    {
        var replacements = new List<string>();
        var committed = new List<string>();
        var liveContext = new LiveBufferRenderContext(80, new TerminalPalette(false));
        var scrollbackContext = new ScrollbackRenderContext(80, liveContext.Palette);

        Task Replace(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            replacements.Add(string.Join('|', items.SelectMany(item => item.Render(liveContext).Lines)
                .Select(static line => line.Text)));
            return Task.CompletedTask;
        }

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            committed.Add(string.Join('|', item.Render(scrollbackContext)));
            return Task.CompletedTask;
        }

        using var error = new StringWriter();
        var view = new EnhancedTurnView(Replace, Commit, error, static () => 80, true, false, new ForegroundTurn(), new ToolPresenterRegistry([], new GenericToolPresenter()));
        var text = new Event { TextChunk = new TextChunk { Fragment = "pending" } };
        await view.Prepare(text, cancellationToken);
        _ = await view.Render(text, cancellationToken);
        var reasoning = new Event { ReasoningChunk = new ReasoningChunk { Fragment = "thinking" } };
        await view.Prepare(reasoning, cancellationToken);
        _ = await view.Render(reasoning, cancellationToken);

        _ = await Assert.That(replacements).Contains("● pending");
        _ = await Assert.That(replacements.Any(static value => value.Contains("thinking", StringComparison.Ordinal))).IsTrue();
        _ = await Assert.That(committed).Contains("● pending");
    }

    [Test]
    public async Task Turn_view_agent_task_progress_replaces_the_previous_tree(
        CancellationToken cancellationToken)
    {
        var replacements = new List<string>();
        var committed = new List<string>();
        var liveContext = new LiveBufferRenderContext(80, new TerminalPalette(false));

        Task Replace(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            replacements.Add(string.Join('|', items.SelectMany(item => item.Render(liveContext).Lines)
                .Select(static line => line.Text)));
            return Task.CompletedTask;
        }

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            committed.Add(string.Join('|', item.Render(new ScrollbackRenderContext(80, liveContext.Palette))));
            return Task.CompletedTask;
        }

        using var error = new StringWriter();
        var view = new EnhancedTurnView(Replace, Commit, error, static () => 80, true, false, new ForegroundTurn(), new ToolPresenterRegistry([], new GenericToolPresenter()));
        _ = await view.Render(new Event { AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot { RootNodes = { new AgentTaskProgressNode { Name = "first", Status = AgentTaskProgressStatus.Running } } } }, cancellationToken);
        _ = await view.Render(new Event { AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot { RootNodes = { new AgentTaskProgressNode { Name = "latest", Status = AgentTaskProgressStatus.Succeeded } } } }, cancellationToken);

        _ = await Assert.That(replacements).Count().IsEqualTo(2);
        _ = await Assert.That(replacements[^1]).Contains("✓ latest");
        _ = await Assert.That(replacements[^1]).DoesNotContain("first");
        _ = await Assert.That(committed).IsEmpty();
    }

    [Test]
    public async Task Cancel_retries_the_same_stream_completion_after_commit_cancellation(
        CancellationToken cancellationToken)
    {
        var committed = new List<IScrollbackItem>();
        var attempts = 0;

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token) => Task.CompletedTask;

        Task Commit(
            IScrollbackItem item,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken token)
        {
            committed.Add(item);
            attempts++;
            return attempts == 2
                ? Task.FromException(new OperationCanceledException())
                : Task.CompletedTask;
        }

        using var error = new StringWriter();
        var view = new EnhancedTurnView(Draw, Commit, error, static () => 80, false, false, new ForegroundTurn(), new ToolPresenterRegistry([], new GenericToolPresenter()));
        _ = await view.Render(
            new Event { TextChunk = new TextChunk { Fragment = "complete line\nsuffix" } },
            cancellationToken);

        _ = await Assert.That(async () => await view.Prepare(
            new Event { TurnEnded = new TurnEnded() },
            cancellationToken)).Throws<OperationCanceledException>();
        await view.Cancel(CancellationToken.None);

        _ = await Assert.That(committed.Count).IsEqualTo(3);
        _ = await Assert.That(ReferenceEquals(committed[1], committed[2])).IsTrue();
        _ = await Assert.That(committed[2].Continues(committed[0])).IsTrue();
    }

    [Test]
    [Timeout(15_000)]
    public async Task Loaded_live_buffer_row_budget_clips_oldest_activity(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(true, "live_buffer_rows: 2\n");
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("prompt");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            TurnStarted = new TurnStarted { Model = "model" },
        });
        var oldest = $"oldest-tail {new string('o', 70)}\n";
        var newest = $"newest-tail {new string('n', 66)}";
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            TextChunk = new TextChunk { Fragment = oldest + newest },
        });
        _ = await driver.FlushedOutputContainsAfter(0, "newest-tail", cancellationToken);

        driver.Resize(81);
        var liveFrameStart = driver.Output.Length;
        driver.Input.Type("x");
        var liveFrame = await driver.FlushedOutputContainsAfter(liveFrameStart, "❯ x", cancellationToken);

        _ = await Assert.That(liveFrame).DoesNotContain("oldest-tail");
        _ = await Assert.That(liveFrame).Contains("newest-tail");
        _ = await Assert.That(liveFrame).Contains("model");
        _ = await Assert.That(liveFrame).Contains("❯ x");

        await driver.Sent(2, cancellationToken);
        driver.Input.End();
        _ = await driving;
    }

    [Test]
    public async Task Interactive_chat_updates_the_prompt_while_a_turn_is_busy(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("first prompt");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event { Id = "start", TurnStarted = new TurnStarted { Model = "model" } });

        driver.Input.Type("steer while busy");
        await driver.OutputContains("steer while busy", cancellationToken);
        await driver.Sent(2, cancellationToken);

        driver.Input.End();
        _ = await driving;
    }

    [Test]
    [Timeout(15_000)]
    public async Task Slash_command_completion_filters_selects_and_dispatches(CancellationToken cancellationToken)
    {
        using var terminal = new ScriptedTerminal(80);
        using var stopping = new CancellationTokenSource();
        using var http = new HttpClient();
        var invoker = new ScriptedInvoker();
        using var loadedConfiguration = new LoadedConfiguration(string.Empty);
        var configuration = loadedConfiguration.Value;
        var presenters = new ToolPresenterRegistry([], new GenericToolPresenter());
        var renderer = new EnhancedTurnRenderer(terminal, configuration, presenters);
        using var diagnostics = new TransportDiagnosticsFixture();
        var cli = new EnhancedCli(
            new GeneratedParrot.ParrotClient(invoker),
            new Interrupts(stopping),
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            new UnusedCredentials(),
            new OpenAiOAuthClient(http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            configuration,
            ["provider"],
            terminal,
            presenters,
            renderer,
            TimeProvider.System,
            ImmediateDelay(),
            new AttachmentsFixture().Uploader,
            diagnostics.Log);
        var running = cli.Run(cancellationToken);

        terminal.Type("/m");
        await OutputContains(terminal, "Switch the mode for this session", cancellationToken);
        await OutputContains(terminal, "Switch the model for this session", cancellationToken);
        var filtered = terminal.Output.ToString() ?? throw new InvalidOperationException("terminal output is unavailable");
        var filteredAt = filtered.LastIndexOf("Switch the mode for this session", StringComparison.Ordinal);
        _ = await Assert.That(filtered[filteredAt..]).DoesNotContain("Leave the session");

        terminal.Type("\u001b[B\t");
        await OutputContains(terminal, "❯ /model", cancellationToken);
        _ = await Assert.That(invoker.Sent).IsEmpty();

        terminal.Type("\u0001\u000b/mo\r");
        await OutputContains(terminal, "Select a mode", cancellationToken);
        terminal.Type("plan\r");
        await OutputContains(terminal, "mode is now plan", cancellationToken);
        terminal.Type("\r");
        _ = await Assert.That(invoker.Updated[0].Mode).IsEqualTo("plan");

        terminal.Type("plain text\r");
        await Sent(invoker, 1, cancellationToken);
        _ = await Assert.That(invoker.Sent[0]).IsEqualTo("plain text");

        terminal.Type("\u0001\u000b/exit\r");
        _ = await running.WaitAsync(cancellationToken);
    }

    [Test]
    public async Task Skill_completion_accepts_and_submits_with_enter(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        driver.Invoker.SetSkills(
            "session-1",
            new Skill { Name = "greet", Path = "/greet", Enabled = true, Description = "greet description" });
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("$gre");
        await driver.OutputContains("greet description", cancellationToken);
        await driver.Sent(1, cancellationToken);

        _ = await Assert.That(driver.Invoker.Sent[0]).IsEqualTo("$greet");

        driver.Input.End();
        _ = await driving;
    }

    [Test]
    [Timeout(15_000)]
    public async Task Shift_tab_cycles_through_foreground_modes(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var driving = driver.Drive(cancellationToken);

        while (driver.Input.Reads < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        driver.Input.Type("\u001b[Z\u001b[Z\u001b[Z");
        while (driver.Invoker.Updated.Count < 3)
        {
            await Task.Delay(5, cancellationToken);
        }

        driver.Input.End();
        _ = await driving;

        _ = await Assert.That(driver.Invoker.Updated.Count).IsEqualTo(3);
        _ = await Assert.That(driver.Invoker.Updated[0].Mode).IsEqualTo("plan");
        _ = await Assert.That(driver.Invoker.Updated[1].Mode).IsEqualTo("query");
        _ = await Assert.That(driver.Invoker.Updated[2].Mode).IsEqualTo("build");
    }

    [Test]
    public async Task Shift_tab_from_query_wraps_to_build(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(
            enhanced: true,
            new EnhancedChatRequest(
                new CreateSessionRequest { Model = "provider/model", Mode = "query" },
                string.Empty));
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("\u001b[Z");
        while (driver.Invoker.Updated.Count < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        driver.Input.End();
        _ = await driving;

        _ = await Assert.That(driver.Invoker.Updated).HasSingleItem();
        _ = await Assert.That(driver.Invoker.Updated[0].Mode).IsEqualTo("build");
    }

    [Test]
    public async Task Queue_inventory_is_visible_before_the_first_turn(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        driver.Invoker.SetInitialQueues(
            "session-1",
            new QueueState { Name = "release", Description = "release tasks", ItemCount = 3 });

        var driving = driver.Drive(cancellationToken);

        await driver.OutputContains("queue: release · 3 items — release tasks", cancellationToken);
        driver.Input.End();
        _ = await driving;
    }

    [Test]
    public async Task Queue_inventory_keeps_more_than_ten_fixed_rows(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        driver.Invoker.SetInitialQueues(
            "session-1",
            [.. Enumerable.Range(1, 12).Select(index => new QueueState
            {
                Name = $"queue-{index:D2}",
                Description = $"work list {index:D2}",
                ItemCount = index,
            })]);

        var driving = driver.Drive(cancellationToken);

        await driver.OutputContains("queue: queue-12 · 12 items — work list 12", cancellationToken);
        driver.Input.End();
        _ = await driving;
    }

    [Test]
    public async Task Queue_inventory_updates_while_turn_text_is_streaming(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("prompt");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event
        {
            Id = "start",
            AgentSessionId = "agent",
            TurnStarted = new TurnStarted { Model = "model" },
        });
        await driver.Invoker.Publish(new Event
        {
            Id = "text",
            AgentSessionId = "agent",
            TextChunk = new TextChunk { Fragment = "partial answer" },
        });
        await driver.Invoker.Publish(new Event
        {
            QueueSnapshot = new QueueSnapshot
            {
                OwnerAgentSessionId = "agent",
                RootAgentSessionId = "agent",
                InventoryInstanceId = "inventory",
                Revision = 1,
                FinalChunk = true,
                Queues = { new QueueState { OwnerAgentSessionId = "agent", Name = "work", Description = "pending work", ItemCount = 2 } },
            },
        });

        await driver.OutputContains("partial answer", cancellationToken);
        await driver.OutputContains("queue: work · 2 items — pending work", cancellationToken);
        await driver.Invoker.Publish(new Event
        {
            QueueSnapshot = new QueueSnapshot { OwnerAgentSessionId = "agent", RootAgentSessionId = "agent", InventoryInstanceId = "inventory", Revision = 2, FinalChunk = true },
        });
        await driver.Invoker.Publish(new Event
        {
            Id = "end",
            AgentSessionId = "agent",
            TurnEnded = new TurnEnded { FinishReason = "stop" },
        });

        driver.Input.End();
        _ = await driving;
    }

    [Test]
    public async Task Spinner_rearms_for_each_turn_and_shutdown_joins_the_active_spinner(
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("first prompt");
        await driver.Sent(1, cancellationToken);
        await driver.OutputContains("preparing turn", cancellationToken);
        await driver.OutputContains("Preparing turn…", cancellationToken);
        await driver.Invoker.Publish(
            new Event { Id = "start-1", AgentSessionId = "agent", TurnStarted = new TurnStarted { Model = "model" } });
        await driver.Invoker.Publish(
            new Event { Id = "ended-1", AgentSessionId = "agent", TurnEnded = new TurnEnded { FinishReason = "stop" } });

        var secondTurnOutput = driver.Output.Length;
        driver.Input.Type("second prompt");
        await driver.Sent(2, cancellationToken);
        await driver.OutputContainsAfter(secondTurnOutput, "preparing turn", cancellationToken);
        await driver.OutputContainsAfter(secondTurnOutput, "Preparing turn…", cancellationToken);

        driver.Input.End();
        _ = await driving.WaitAsync(cancellationToken);
    }

    [Test]
    public async Task Interactive_chat_accepts_another_turn_after_a_failure(CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced: true);
        var driving = driver.Drive(cancellationToken);

        driver.Input.Type("first prompt");
        await driver.Sent(1, cancellationToken);
        await driver.Invoker.Publish(new Event { Id = "start-1", TurnStarted = new TurnStarted { Model = "model" } });
        await driver.Invoker.Publish(
            new Event { Id = "failed-1", TurnFailed = new TurnFailed { Message = "tool-call limit" } });
        await driver.ErrorContains("tool-call limit", cancellationToken);

        driver.Input.Type("second prompt");
        await driver.Sent(2, cancellationToken);
        await driver.Invoker.Publish(new Event { Id = "start-2", TurnStarted = new TurnStarted { Model = "model" } });
        await driver.Invoker.Publish(
            new Event { Id = "text-2", TextChunk = new TextChunk { Fragment = "recovered answer" } });
        await driver.Invoker.Publish(
            new Event { Id = "ended-2", TurnEnded = new TurnEnded { FinishReason = "stop" } });

        await driver.OutputContains("recovered answer", cancellationToken);
        driver.Input.End();
        _ = await driving;

        _ = await Assert.That(driver.Invoker.Sent.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Live_rates_appear_expire_and_clip_through_enhanced_cli(CancellationToken cancellationToken)
    {
        var time = new ControlledTimeProvider();
        using var driver = new CliLifecycleDriver(
            true,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            ImmediateDelay(),
            time,
            32);
        var driving = driver.Drive(cancellationToken);

        await driver.Invoker.Publish(new Event
        {
            ProviderCallUsage = new ProviderCallUsage { InputTokens = 300, OutputTokens = 60 },
        });
        await driver.Invoker.Publish(new Event { TurnStarted = new TurnStarted { Model = "model" } });
        await driver.OutputContains("10i/s 2o/s", cancellationToken);
        _ = await Assert.That(driver.Output).Contains("─ mode: build ─");

        var expiryAt = driver.Output.Length;
        time.Advance(TimeSpan.FromSeconds(30));
        await driver.OutputContainsAfter(expiryAt, "provider/model", cancellationToken);
        _ = await Assert.That(driver.Output[expiryAt..]).DoesNotContain("i/s");

        driver.Input.Type("/exit");
        _ = await driving.WaitAsync(cancellationToken);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Slash_dialog_restores_input_rebinds_the_stream_and_exit_unwinds_terminal_cleanup(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        using var driver = new CliLifecycleDriver(enhanced);
        var driving = driver.Drive(cancellationToken);

        if (enhanced)
        {
            await driver.Invoker.Publish(new Event
            {
                ProviderCallUsage = new ProviderCallUsage { InputTokens = 300, OutputTokens = 60 },
            });
            await driver.OutputContains("10i/s 2o/s", cancellationToken);
        }

        var replacementOutput = driver.Output.Length;
        driver.Input.Type("/clear");
        if (enhanced)
        {
            await driver.OutputContainsAfter(replacementOutput, "Select a provider", cancellationToken);
        }

        driver.Input.Type("provider");
        driver.Input.Type("model");
        driver.Input.Type("query");
        driver.Input.Type(string.Empty);
        if (enhanced)
        {
            await driver.OutputContainsAfter(replacementOutput, "mode: query", cancellationToken);
            _ = await Assert.That(driver.Output[replacementOutput..]).DoesNotContain("i/s");
            var replacementRateOutput = driver.Output.Length;
            await driver.Invoker.Publish("session-2", new Event
            {
                ProviderCallUsage = new ProviderCallUsage { InputTokens = 150, OutputTokens = 30 },
            });
            await driver.OutputContainsAfter(replacementRateOutput, "5i/s 1o/s", cancellationToken);
        }

        driver.Input.Type("new prompt");
        await driver.Sent(1, cancellationToken);
        driver.Input.Type("/exit");

        var exitCode = await driving.WaitAsync(cancellationToken);

        _ = await Assert.That(exitCode).IsEqualTo(CommandDispatcher.ExitSuccess);
        _ = await Assert.That(driver.Invoker.Created.Count).IsEqualTo(2);
        _ = await Assert.That(driver.Invoker.Created[1].Model).IsEqualTo("provider/model");
        _ = await Assert.That(driver.Invoker.Created[1].Mode).IsEqualTo("query");
        _ = await Assert.That(driver.Invoker.Created[0].InteractivePermissions).IsTrue();
        _ = await Assert.That(driver.Invoker.Created[1].InteractivePermissions).IsTrue();
        _ = await Assert.That(string.Join('|', driver.Invoker.ListenedTo)).IsEqualTo("session-1|session-2");
        _ = await Assert.That(string.Join('|', driver.Invoker.Sent)).IsEqualTo("new prompt");
        _ = await Assert.That(string.Join('|', driver.Invoker.SentTo)).IsEqualTo("session-2");
        if (enhanced)
        {
            _ = await Assert.That(driver.Output).EndsWith("\u001b[<u\u001b[?2004l");
        }
    }

    private static Func<TimeSpan, CancellationToken, Task> ImmediateDelay() =>
        static (_, cancellationToken) => Task.Delay(1, cancellationToken);

    private static async Task PublishQuestionStart(CliLifecycleDriver driver)
    {
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            TurnStarted = new TurnStarted { Model = "model" },
        });
        await driver.Invoker.Publish(new Event
        {
            AgentSessionId = "agent",
            ToolStarted = new ToolStarted { ToolCallId = "call-question", ToolName = "question" },
        });
    }

    private static async Task WaitForQuestionList(
        CliLifecycleDriver driver,
        int count,
        CancellationToken cancellationToken)
    {
        while (driver.Invoker.PendingQuestionLists < count)
        {
            await Task.Delay(5, cancellationToken);
        }
    }

    private static async Task OutputContains(
        ScriptedTerminal terminal, string text, CancellationToken cancellationToken)
    {
        while (!(terminal.Output.ToString() ?? string.Empty).Contains(text, StringComparison.Ordinal))
        {
            await Task.Delay(5, cancellationToken);
        }
    }

    private static async Task Sent(ScriptedInvoker invoker, int count, CancellationToken cancellationToken)
    {
        while (invoker.Sent.Count < count)
        {
            await Task.Delay(5, cancellationToken);
        }
    }

    private sealed class QuestionFixture
    {
        public QuestionFixture(string requestId, string prompt)
        {
            var pending = new PendingQuestion { Id = requestId };
            pending.Questions.Add(new QuestionDefinition
            {
                Header = "Question",
                Prompt = prompt,
                Options = { "One" },
            });
            Pending = pending;
        }

        public PendingQuestion Pending { get; }
    }

    private sealed class AttachmentsFixture
    {
        public AttachmentsFixture()
        {
            var profiles = new Dictionary<string, ProfileConfig>(StringComparer.Ordinal)
            {
                [ModeRegistry.Build] = new(string.Empty, string.Empty, null, 1, 1, false, false, true, false, []),
                [ModeRegistry.Plan] = new(string.Empty, string.Empty, null, 1, 1, false, false, true, false, []),
                [ModeRegistry.Query] = new(string.Empty, string.Empty, null, 1, 1, false, false, true, false, []),
            };
            Uploader = new PromptAttachmentUploader(
                new ToolWorkspace(Directory.GetCurrentDirectory()),
                new ModeRegistry(new ProfileRegistry(profiles, [], [], new HashSet<string>(StringComparer.Ordinal)), ModeRegistry.Build));
        }

        public PromptAttachmentUploader Uploader { get; }
    }

    private sealed class LoadedConfiguration : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "parrot-cli-tests",
            Guid.NewGuid().ToString("N"));

        public LoadedConfiguration(string content)
        {
            _ = Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "config.yaml");
            File.WriteAllText(path, content);
            Value = Configuration.Load(path, Path.Combine(_directory, "predefined_config.yaml"));
        }

        public Configuration Value { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class ControlledSubmitDelay
    {
        private readonly Channel<PendingDelay> _pending = Channel.CreateUnbounded<PendingDelay>();

        public Task Wait(TimeSpan interval, CancellationToken cancellationToken)
        {
            var pending = new PendingDelay(interval, cancellationToken);
            if (!_pending.Writer.TryWrite(pending))
            {
                throw new InvalidOperationException("unable to queue the submit delay");
            }

            return pending.Wait();
        }

        public ValueTask<PendingDelay> Read(CancellationToken cancellationToken) =>
            _pending.Reader.ReadAsync(cancellationToken);
    }

    private sealed class PendingDelay(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Canceled { get; } = ObserveCancellation(cancellationToken);

        public TimeSpan Interval { get; } = interval;

        public void Release() => _released.TrySetResult();

        public Task Wait() => _released.Task.WaitAsync(cancellationToken);

        private static async Task ObserveCancellation(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly List<ControlledTimer> _timers = [];
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControlledTimer(callback, state, _timestamp + dueTime.Ticks);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            _timestamp += elapsed.Ticks;
            foreach (var timer in _timers.Where(timer => timer.DueTimestamp <= _timestamp).ToArray())
            {
                timer.Fire();
            }
        }

        private sealed class ControlledTimer(TimerCallback callback, object? state, long dueTimestamp) : ITimer
        {
            public long DueTimestamp { get; } = dueTimestamp;

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            public void Fire() => callback(state);
        }
    }
}
