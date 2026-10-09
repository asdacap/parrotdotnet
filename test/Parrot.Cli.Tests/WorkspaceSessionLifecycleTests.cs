using Grpc.Core;
using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class WorkspaceSessionLifecycleTests
{
    private const string OldId = "user-session-old";
    private const string SelectedId = "user-session-selected";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [Timeout(40_000)]
    public async Task Picker_replays_history_and_routes_messages_commands_and_clear_to_selected_host(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        var old = new ScriptedInvoker();
        var selected = new ScriptedInvoker { StatusText = "selected host status" };
        selected.SetSkills(SelectedId, new Skill { Name = "selected-skill", Path = "/selected/SKILL.md", Description = "selected skill inventory", Enabled = true });
        using var navigation = new HostNavigation(old, selected);
        using var driver = CreateDriver(enhanced, old, navigation);
        var running = driver.Drive(cancellationToken);
        await WaitUntil(() => driver.Input.Reads > 0, cancellationToken);

        await SelectSession(driver, SelectedId, cancellationToken);
        await driver.OutputContains("selected saved history", cancellationToken);
        _ = await Assert.That(selected.Sent).IsEmpty();
        _ = await Assert.That(old.Sent).IsEmpty();
        _ = await Assert.That(selected.Created).IsEmpty();
        _ = await Assert.That(old.Created).IsEmpty();
        if (enhanced)
        {
            _ = await Assert.That(selected.SkillLists(SelectedId)).IsGreaterThan(0);
        }

        var stage = "attachment";
        var image = Path.Combine(Path.GetTempPath(), "parrot-picker-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var pixels = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
            await File.WriteAllBytesAsync(image, pixels, cancellationToken);
            driver.Input.Type($"inspect @{{{image}}}");
            await WaitUntil(() => selected.Sent.Count == 1, cancellationToken);
            _ = await Assert.That(selected.SentTo.Single()).IsEqualTo(SelectedId);
            _ = await Assert.That(selected.UploadedAttachments[0].Header.UserSessionId).IsEqualTo(SelectedId);
            _ = await Assert.That(old.UploadedAttachments).IsEmpty();
            await CompleteTurn(selected, SelectedId, "selected response", cancellationToken);
            await driver.OutputContains("selected response", cancellationToken);

            stage = "status";
            driver.Input.Type("/status");
            await WaitUntil(() => selected.Statuses.Count == 1, cancellationToken);
            await driver.OutputContains("selected host status", cancellationToken);
            _ = await Assert.That(selected.Statuses.Single().UserSessionId).IsEqualTo(SelectedId);
            _ = await Assert.That(old.Statuses).IsEmpty();

            stage = "model";
            driver.Input.Type("/model");
            await Choose(driver, "Select a provider", "provider", cancellationToken);
            await Choose(driver, "Select a model", "other", cancellationToken);
            await WaitUntil(() => selected.Updated.Count == 1, cancellationToken);
            await DismissMessage(driver, enhanced, "model is now provider/other", cancellationToken);
            _ = await Assert.That(selected.Updated[0].UserSessionId).IsEqualTo(SelectedId);
            _ = await Assert.That(selected.Updated[0].Model).IsEqualTo("provider/other");

            stage = "mode";
            driver.Input.Type("/mode");
            await Choose(driver, enhanced ? "Select a mode " : "Select a mode" + Environment.NewLine, "plan", cancellationToken);
            await WaitUntil(() => selected.Updated.Count == 2, cancellationToken);
            await DismissMessage(driver, enhanced, "mode is now plan", cancellationToken);
            _ = await Assert.That(selected.Updated[1].UserSessionId).IsEqualTo(SelectedId);
            _ = await Assert.That(selected.Updated[1].Mode).IsEqualTo("plan");
            _ = await Assert.That(old.Updated).IsEmpty();

            var skillsBefore = selected.SkillLists(SelectedId);
            stage = "skills";
            driver.Input.Type("/skills");
            await Choose(driver, "Skills", enhanced ? "List skills" : "list", cancellationToken);
            await WaitUntil(() => selected.SkillLists(SelectedId) > skillsBefore, cancellationToken);
            await DismissMessage(driver, enhanced, "selected skill inventory", cancellationToken);

            stage = "clear";
            var providersAt = driver.Output.Length;
            driver.Input.Type("/clear");
            await driver.OutputContainsAfter(providersAt, "Select a provider", cancellationToken);
            driver.Input.Type("provider");
            await driver.OutputContainsAfter(providersAt, "Select a model", cancellationToken);
            driver.Input.Type("model");
            await driver.OutputContainsAfter(providersAt, enhanced ? "Select a mode " : "Select a mode" + Environment.NewLine, cancellationToken);
            driver.Input.Type("build");
            await WaitUntil(() => selected.Created.Count == 1, cancellationToken);
            await DismissMessage(driver, enhanced, "new session session-1", cancellationToken);
            _ = await Assert.That(old.Created).IsEmpty();

            stage = "return to old host";
            await SelectSession(driver, OldId, cancellationToken);
            await driver.OutputContains("old saved history", cancellationToken);
            driver.Input.Type("back on original");
            await WaitUntil(() => old.Sent.Count == 1, cancellationToken);
            _ = await Assert.That(old.SentTo.Single()).IsEqualTo(OldId);
            _ = await Assert.That(selected.Sent.Count).IsEqualTo(1);
            _ = await Assert.That(navigation.Opened).IsEquivalentTo([SelectedId, OldId]);
            driver.Input.End();
            _ = await running.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"Lifecycle stage: {stage}; old sent={old.Sent.Count}; selected sent={selected.Sent.Count}; selected updated={selected.Updated.Count}; created={selected.Created.Count}. Output: {driver.Output}");
            throw;
        }
        finally
        {
            File.Delete(image);
            driver.Input.End();
            _ = await running.WaitAsync(cancellationToken);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [Timeout(20_000)]
    public async Task Attached_mid_turn_history_blocks_next_switch_until_live_mode_completion(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        var old = new ScriptedInvoker();
        var selected = new ScriptedInvoker();
        using var navigation = new HostNavigation(old, selected) { AttachMidTurn = true };
        using var driver = CreateDriver(enhanced, old, navigation);
        var running = driver.Drive(cancellationToken);
        await WaitUntil(() => driver.Input.Reads > 0, cancellationToken);
        await SelectSession(driver, SelectedId, cancellationToken);
        await driver.OutputContains("selected saved history", cancellationToken);
        await SelectSession(driver, OldId, cancellationToken);
        await Task.Delay(150, cancellationToken);
        _ = await Assert.That(navigation.Opened).IsEquivalentTo([SelectedId]);

        await selected.Publish(SelectedId, new Event { AgentSessionId = "main", TurnEnded = new TurnEnded() });
        await Task.Delay(150, cancellationToken);
        _ = await Assert.That(navigation.Opened).IsEquivalentTo([SelectedId]);
        await selected.Publish(SelectedId, new Event { AgentSessionId = "main", ModeTurnCompleted = new ModeTurnCompleted() });
        await driver.OutputContains("old saved history", cancellationToken);
        _ = await Assert.That(navigation.Opened).IsEquivalentTo([SelectedId, OldId]);
        driver.Input.End();
        _ = await running.WaitAsync(cancellationToken);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [Timeout(30_000)]
    public async Task Selected_host_receives_question_permission_and_interrupt_requests(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        var old = new ScriptedInvoker();
        var selected = new ScriptedInvoker();
        using var navigation = new HostNavigation(old, selected);
        using var driver = CreateDriver(enhanced, old, navigation);
        var running = driver.Drive(cancellationToken);
        await WaitUntil(() => driver.Input.Reads > 0, cancellationToken);
        await SelectSession(driver, SelectedId, cancellationToken);
        await driver.OutputContains("selected saved history", cancellationToken);

        var question = new PendingQuestion
        {
            Id = "selected-question",
            Questions =
            {
                new QuestionDefinition
                {
                    Header = "Selected decision",
                    Prompt = "Choose on selected host",
                    Options = { new QuestionOption { Label = "One" } },
                },
            },
        };
        selected.AddPendingQuestion(SelectedId, question);
        await driver.OutputContains("Choose on selected host", cancellationToken);
        driver.Input.Type(enhanced ? string.Empty : "one");
        await WaitUntil(() => selected.QuestionReplies.Count == 1, cancellationToken);
        _ = await Assert.That(selected.QuestionReplies.Single().UserSessionId).IsEqualTo(SelectedId);
        _ = await Assert.That(old.QuestionReplies).IsEmpty();

        var permission = new PendingPermission
        {
            Id = "selected-permission",
            AgentSessionId = "main",
            Reason = "Allow selected host write",
            Choices =
            {
                new PermissionChoice { Value = "allow", Label = "Allow", Action = PermissionAction.Allow },
                new PermissionChoice { Value = "deny", Label = "Deny", Action = PermissionAction.Deny },
            },
        };
        await selected.Publish(SelectedId, new Event { PermissionPending = permission });
        await driver.OutputContains("Allow selected host write", cancellationToken);
        driver.Input.Type(enhanced ? string.Empty : "allow");
        await WaitUntil(() => selected.PermissionReplies.Count == 1, cancellationToken);
        _ = await Assert.That(selected.PermissionReplies.Single().UserSessionId).IsEqualTo(SelectedId);
        _ = await Assert.That(old.PermissionReplies).IsEmpty();

        driver.Input.Type("interrupt selected work");
        await WaitUntil(() => selected.Sent.Count == 1, cancellationToken);
        await selected.Publish(SelectedId, new Event { TurnStarted = new TurnStarted { Model = "model" } });
        driver.Interrupts.Signal();
        await WaitUntil(() => selected.Interrupts == 1, cancellationToken);
        _ = await Assert.That(old.Interrupts).IsEqualTo(0);
        _ = await Assert.That(driver.Stopping.IsCancellationRequested).IsFalse();
        driver.Input.End();
        _ = await running.WaitAsync(cancellationToken);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [Timeout(20_000)]
    public async Task Deferred_candidate_stream_failure_leaves_previous_host_usable_without_candidate_history(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        var old = new ScriptedInvoker();
        var selected = new ScriptedInvoker { ListenFailure = StatusCode.Unavailable };
        using var navigation = new HostNavigation(old, selected);
        using var driver = CreateDriver(enhanced, old, navigation);
        var running = driver.Drive(cancellationToken);
        await WaitUntil(() => driver.Input.Reads > 0, cancellationToken);
        await SelectSession(driver, SelectedId, cancellationToken);
        if (enhanced)
        {
            await driver.OutputContains("selected stream failed", cancellationToken);
            driver.Input.Type(string.Empty);
        }
        else
        {
            await driver.ErrorContains("selected stream failed", cancellationToken);
        }

        _ = await Assert.That(driver.Output).DoesNotContain("selected saved history");
        driver.Input.Type("still original");
        await WaitUntil(() => old.Sent.Count == 1, cancellationToken);
        _ = await Assert.That(old.SentTo.Single()).IsEqualTo(OldId);
        _ = await Assert.That(selected.Sent).IsEmpty();
        _ = await Assert.That(selected.Created).IsEmpty();
        _ = await Assert.That(old.Created).IsEmpty();
        driver.Input.End();
        _ = await running.WaitAsync(cancellationToken);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [Timeout(20_000)]
    public async Task Failure_tearing_down_old_interactions_after_candidate_readiness_restores_original_host(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        var old = new ScriptedInvoker { FailPermissionCancellationOnce = true };
        var selected = new ScriptedInvoker();
        using var navigation = new HostNavigation(old, selected);
        using var driver = CreateDriver(enhanced, old, navigation);
        var running = driver.Drive(cancellationToken);
        await old.PermissionCancellationPending.Task.WaitAsync(cancellationToken);
        await WaitUntil(() => driver.Input.Reads > 0, cancellationToken);
        await SelectSession(driver, SelectedId, cancellationToken);
        if (enhanced)
        {
            await driver.OutputContains("old reconciliation teardown failed", cancellationToken);
            driver.Input.Type(string.Empty);
        }
        else
        {
            await driver.ErrorContains("old reconciliation teardown failed", cancellationToken);
        }

        _ = await Assert.That(selected.ListenedTo).IsEquivalentTo([SelectedId]);
        _ = await Assert.That(driver.Output).DoesNotContain("selected saved history");
        driver.Input.Type("work after readiness rollback");
        await WaitUntil(() => old.Sent.Count == 1, cancellationToken);
        _ = await Assert.That(old.SentTo.Single()).IsEqualTo(OldId);
        _ = await Assert.That(selected.Sent).IsEmpty();
        _ = await Assert.That(old.Created).IsEmpty();
        _ = await Assert.That(selected.Created).IsEmpty();
        driver.Input.End();
        _ = await running.WaitAsync(cancellationToken);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [Timeout(20_000)]
    public async Task Historical_plan_and_failed_turn_do_not_prompt_or_stop_later_history(
        bool enhanced,
        CancellationToken cancellationToken)
    {
        var old = new ScriptedInvoker();
        var selected = new ScriptedInvoker();
        using var navigation = new HostNavigation(old, selected) { ReplayFailures = true };
        using var driver = CreateDriver(enhanced, old, navigation);
        var running = driver.Drive(cancellationToken);
        await WaitUntil(() => driver.Input.Reads > 0, cancellationToken);
        await SelectSession(driver, SelectedId, cancellationToken);
        await driver.OutputContains("later saved assistant history", cancellationToken);

        _ = await Assert.That(driver.Output).DoesNotContain("Historical plan action must not open");
        _ = await Assert.That(selected.Sent).IsEmpty();
        _ = await Assert.That(selected.QuestionReplies).IsEmpty();
        driver.Input.Type("new live work after history");
        await WaitUntil(() => selected.Sent.Count == 1, cancellationToken);
        _ = await Assert.That(selected.SentTo.Single()).IsEqualTo(SelectedId);
        _ = await Assert.That(old.Sent).IsEmpty();
        driver.Input.End();
        _ = await running.WaitAsync(cancellationToken);
    }

    private static CliLifecycleDriver CreateDriver(bool enhanced, ScriptedInvoker old, ITerminalSessionNavigation navigation) =>
        new(enhanced, new EnhancedChatRequest(new CreateSessionRequest(), string.Empty)
        {
            InitialSession = new UserSession { Id = OldId, Model = "provider/model", Mode = "build", WorkingDirectory = "/server/workspace" },
        })
        {
            InitialInvoker = old,
            Navigation = navigation,
        };

    private static async Task SelectSession(CliLifecycleDriver driver, string id, CancellationToken cancellationToken)
    {
        var start = driver.Output.Length;
        driver.Input.Type("/workspace-sessions");
        await driver.OutputContainsAfter(start, "Workspace sessions", cancellationToken);
        driver.Input.Type(id);
    }

    private static async Task Choose(CliLifecycleDriver driver, string title, string choice, CancellationToken cancellationToken)
    {
        await driver.OutputContains(title, cancellationToken);
        driver.Input.Type(choice);
    }

    private static async Task DismissMessage(CliLifecycleDriver driver, bool enhanced, string message, CancellationToken cancellationToken)
    {
        await driver.OutputContains(message, cancellationToken);
        if (enhanced)
        {
            driver.Input.Type(string.Empty);
        }
    }

    private static async Task WaitUntil(Func<bool> observed, CancellationToken cancellationToken)
    {
        while (!observed())
        {
            await Task.Delay(5, cancellationToken);
        }
    }

    private static async Task CompleteTurn(ScriptedInvoker host, string id, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await host.Publish(id, new Event { AgentSessionId = "main", TurnStarted = new TurnStarted { Model = "model" } });
        await host.Publish(id, new Event { AgentSessionId = "main", TextChunk = new TextChunk { Fragment = text } });
        await host.Publish(id, new Event { AgentSessionId = "main", TurnEnded = new TurnEnded { FinishReason = "stop" } });
        await host.Publish(id, new Event { AgentSessionId = "main", ModeTurnCompleted = new ModeTurnCompleted() });
    }

    private static IReadOnlyList<Event> CreateHistory(string text) =>
    [
        new Event { Id = "history-start", AgentSessionId = "main", TurnStarted = new TurnStarted { Model = "model" } },
        new Event { Id = "history-text", AgentSessionId = "main", TextChunk = new TextChunk { Fragment = text } },
        new Event { Id = "history-end", AgentSessionId = "main", TurnEnded = new TurnEnded { FinishReason = "stop" } },
        new Event { Id = "history-complete", AgentSessionId = "main", ModeTurnCompleted = new ModeTurnCompleted() },
    ];

    private sealed class HostNavigation(ScriptedInvoker old, ScriptedInvoker selected) : ITerminalSessionNavigation
    {
        public List<string> Opened { get; } = [];

        public bool ReplayFailures { get; init; }

        public bool AttachMidTurn { get; init; }

        public Task<ListSessionsResponse> List(UserSession current, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listed = new ListSessionsResponse();
            listed.Sessions.AddRange([
                new SessionSummary { UserSessionId = SelectedId, RootAgentName = "selected", State = SessionState.Active, CreatedAt = "2026-07-28T02:00:00Z" },
                new SessionSummary { UserSessionId = OldId, RootAgentName = "original", State = SessionState.Active, CreatedAt = "2026-07-28T01:00:00Z" },
            ]);
            return Task.FromResult(listed);
        }

        public Task<TerminalSessionTarget> Open(UserSession current, string userSessionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opened.Add(userSessionId);
            var host = userSessionId == OldId ? old : selected;
            var text = userSessionId == OldId ? "old saved history" : "selected saved history";
            var history = CreateHistory(text).ToList();
            if (AttachMidTurn && userSessionId == SelectedId)
            {
                history.RemoveRange(history.Count - 2, 2);
            }

            if (ReplayFailures && userSessionId == SelectedId)
            {
                var completed = history[^1];
                history.RemoveAt(history.Count - 1);
                history.Add(new Event
                {
                    Id = "history-plan",
                    AgentSessionId = "main",
                    PlanCompleted = new PlanCompleted
                    {
                        Markdown = "# Previously completed plan",
                        Dialog = new TurnCompleteDialog
                        {
                            Prompt = "Historical plan action must not open",
                            Choices = { new DialogChoice { Value = "implement", Description = "Implement", Action = new ChoiceAction { Prompt = "historical action must not send" } } },
                        },
                    },
                });
                history.Add(completed);
                history.Add(new Event { Id = "history-failed-start", AgentSessionId = "main", TurnStarted = new TurnStarted { Model = "model" } });
                history.Add(new Event { Id = "history-failed", AgentSessionId = "main", TurnFailed = new TurnFailed { Message = "old saved failure" } });
                history.AddRange(CreateHistory("later saved assistant history").Select(published =>
                {
                    var later = published.Clone();
                    later.Id = "later-" + later.Id;
                    return later;
                }));
            }

            host.SetReplayHistory(userSessionId, history);
            var replacement = new UserSession
            {
                Id = userSessionId,
                Model = "provider/model",
                Mode = "build",
                WorkingDirectory = "/server/workspace",
                Loaded = true,
            };
            return Task.FromResult(new TerminalSessionTarget(host, replacement, null));
        }

        public void Dispose()
        {
        }
    }
}
