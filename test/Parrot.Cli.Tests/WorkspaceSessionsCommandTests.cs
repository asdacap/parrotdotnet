using System.Globalization;
using Grpc.Core;
using Parrot.Cli.Commands;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class WorkspaceSessionsCommandTests
{
    [Test]
    public async Task Lists_creation_instants_newest_first_with_stable_ids_and_local_times(CancellationToken cancellationToken)
    {
        var navigation = new RecordingNavigation();
        navigation.Sessions.Sessions.AddRange([
            new SessionSummary { UserSessionId = "missing", State = SessionState.Corrupt },
            new SessionSummary { UserSessionId = "bad", CreatedAt = "broken" },
            new SessionSummary { UserSessionId = "older", CreatedAt = "2026-07-28T00:00:00Z", State = SessionState.Inactive },
            new SessionSummary { UserSessionId = "tie-b", CreatedAt = "2026-07-28T03:00:00+02:00", State = SessionState.Active },
            new SessionSummary { UserSessionId = "session-0", RootAgentName = "main", CreatedAt = "2026-07-28T01:00:00Z", State = SessionState.Active },
        ]);
        var dialog = new TestSlashDialog().Select((string?)null);
        ISlashCommand command = new WorkspaceSessionsCommand(navigation, new TestSlashSession("model"), new TestSlashActivity(), dialog);

        await command.Run(string.Empty, cancellationToken);

        var options = dialog.Pickers.Single().Options;
        _ = await Assert.That(options.Select(option => option.Id)).IsEquivalentTo(["session-0", "tie-b", "older", "bad", "missing"]);
        _ = await Assert.That(string.Join('|', options.Select(option => option.Id))).IsEqualTo("session-0|tie-b|older|bad|missing");
        _ = await Assert.That(options[0].Label).IsEqualTo("* main  session-0");
        _ = await Assert.That(options[1].Label).IsEqualTo("  <unnamed>  tie-b");
        var created = DateTimeOffset.Parse("2026-07-28T01:00:00Z", CultureInfo.InvariantCulture);
        _ = await Assert.That(options[0].Description).IsEqualTo("active  " + created.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        _ = await Assert.That(options[2].Description).StartsWith("inactive  ");
        _ = await Assert.That(options[3].Description).IsEqualTo("unknown  unknown creation time");
        _ = await Assert.That(options[4].Description).IsEqualTo("corrupt  unknown creation time");
        _ = await Assert.That(navigation.Lists).IsEqualTo(1);
        _ = await Assert.That(navigation.Loaded).IsEmpty();
    }

    [Test]
    [Arguments(null)]
    [Arguments("session-0")]
    [Arguments("corrupt")]
    public async Task Dismissal_current_and_corrupt_selection_do_not_wait_or_load(string? selected, CancellationToken cancellationToken)
    {
        var navigation = new RecordingNavigation();
        navigation.Sessions.Sessions.AddRange([
            new SessionSummary { UserSessionId = "session-0" },
            new SessionSummary { UserSessionId = "corrupt", State = SessionState.Corrupt },
        ]);
        var activity = new TestSlashActivity();
        var dialog = new TestSlashDialog().Select(selected);
        ISlashCommand command = new WorkspaceSessionsCommand(navigation, new TestSlashSession("model"), activity, dialog);

        await command.Run(string.Empty, cancellationToken);

        _ = await Assert.That(navigation.Loaded).IsEmpty();
        _ = await Assert.That(activity.Waits).IsEqualTo(0);
        _ = await Assert.That(dialog.Errors.Count).IsEqualTo(selected == "corrupt" ? 1 : 0);
    }

    [Test]
    public async Task Empty_list_shows_message_without_picker(CancellationToken cancellationToken)
    {
        var navigation = new RecordingNavigation();
        var dialog = new TestSlashDialog();
        ISlashCommand command = new WorkspaceSessionsCommand(navigation, new TestSlashSession("model"), new TestSlashActivity(), dialog);

        await command.Run(string.Empty, cancellationToken);

        _ = await Assert.That(dialog.Shown).IsEquivalentTo(["No sessions in the current workspace."]);
        _ = await Assert.That(dialog.Pickers).IsEmpty();
    }

    [Test]
    public async Task Selected_id_is_loaded_only_after_idle(CancellationToken cancellationToken)
    {
        var navigation = new RecordingNavigation();
        navigation.Sessions.Sessions.Add(new SessionSummary { UserSessionId = "selected", RootAgentName = "not-an-id" });
        var dialog = new TestSlashDialog().Select("selected");
        var activity = new WaitingActivity();
        ISlashCommand command = new WorkspaceSessionsCommand(navigation, new TestSlashSession("model"), activity, dialog);
        var running = command.Run(string.Empty, cancellationToken);
        await activity.Started.Task.WaitAsync(cancellationToken);
        _ = await Assert.That(navigation.Loaded).IsEmpty();
        activity.Idle.SetResult();
        await running.WaitAsync(cancellationToken);

        _ = await Assert.That(navigation.Loaded).IsEquivalentTo(["selected"]);
        _ = await Assert.That(dialog.Loads).IsEquivalentTo(["Loading workspace sessions…", "Loading selected session…"]);
    }

    [Test]
    public async Task Cancellation_while_waiting_preserves_session_without_error(CancellationToken cancellationToken)
    {
        var navigation = new RecordingNavigation();
        navigation.Sessions.Sessions.Add(new SessionSummary { UserSessionId = "selected" });
        var dialog = new TestSlashDialog().Select("selected");
        var activity = new WaitingActivity();
        var session = new TestSlashSession("model");
        ISlashCommand command = new WorkspaceSessionsCommand(navigation, session, activity, dialog);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = command.Run(string.Empty, stopping.Token);
        await activity.Started.Task.WaitAsync(cancellationToken);
        await stopping.CancelAsync();
        _ = await Assert.That(async () => await running.WaitAsync(CancellationToken.None)).Throws<OperationCanceledException>();

        _ = await Assert.That(navigation.Loaded).IsEmpty();
        _ = await Assert.That(session.Id).IsEqualTo("session-0");
        _ = await Assert.That(dialog.Errors).IsEmpty();
    }

    [Test]
    [Arguments(false, StatusCode.Unimplemented)]
    [Arguments(true, StatusCode.Unimplemented)]
    [Arguments(true, StatusCode.AlreadyExists)]
    [Arguments(false, StatusCode.InvalidArgument)]
    public async Task Rpc_failures_report_errors_without_changing_current_session(bool loading, StatusCode status, CancellationToken cancellationToken)
    {
        var failure = new RpcException(new Status(status, "selected session failed"));
        var navigation = new RecordingNavigation { ListFailure = loading ? null : failure, LoadFailure = loading ? failure : null };
        navigation.Sessions.Sessions.Add(new SessionSummary { UserSessionId = "selected" });
        var session = new TestSlashSession("model");
        var dialog = new TestSlashDialog().Select("selected");
        ISlashCommand command = new WorkspaceSessionsCommand(navigation, session, new TestSlashActivity(), dialog);

        await command.Run(string.Empty, cancellationToken);

        _ = await Assert.That(dialog.Errors).IsEquivalentTo([status == StatusCode.Unimplemented
            ? "workspace session selection is unavailable on this server"
            : "selected session failed"]);
        _ = await Assert.That(session.Id).IsEqualTo("session-0");
    }

    [Test]
    public async Task Missing_remote_workspace_reports_unsupported_message(CancellationToken cancellationToken)
    {
        var navigation = new RecordingNavigation { ListFailure = new InvalidOperationException("workspace unavailable") };
        var dialog = new TestSlashDialog();
        ISlashCommand command = new WorkspaceSessionsCommand(navigation, new TestSlashSession("model"), new TestSlashActivity(), dialog);

        await command.Run(string.Empty, cancellationToken);

        _ = await Assert.That(dialog.Errors).IsEquivalentTo(["workspace unavailable"]);
        _ = await Assert.That(navigation.Loaded).IsEmpty();
    }

    [Test]
    public async Task Rpc_cancellation_is_not_presented_as_load_failure(CancellationToken cancellationToken)
    {
        var navigation = new RecordingNavigation { ListFailure = new RpcException(new Status(StatusCode.Cancelled, "cancelled")) };
        var dialog = new TestSlashDialog();
        ISlashCommand command = new WorkspaceSessionsCommand(navigation, new TestSlashSession("model"), new TestSlashActivity(), dialog);

        _ = await Assert.That(async () => await command.Run(string.Empty, cancellationToken)).Throws<RpcException>();
        _ = await Assert.That(dialog.Errors).IsEmpty();
    }

    private sealed class RecordingNavigation : ITerminalSessionController
    {
        public ListSessionsResponse Sessions { get; } = new();

        public List<string> Loaded { get; } = [];

        public int Lists { get; private set; }

        public Exception? ListFailure { get; init; }

        public Exception? LoadFailure { get; init; }

        public Task<ListSessionsResponse> List(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Lists++;
            return ListFailure is { } failure ? Task.FromException<ListSessionsResponse>(failure) : Task.FromResult(Sessions);
        }

        public Task Load(string userSessionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return LoadFailure is { } failure ? Task.FromException(failure) : Record(userSessionId);
        }

        private Task Record(string userSessionId)
        {
            Loaded.Add(userSessionId);
            return Task.CompletedTask;
        }
    }

    private sealed class WaitingActivity : ISlashActivity
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Idle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitUntilIdle(CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Idle.Task.WaitAsync(cancellationToken);
        }
    }
}
