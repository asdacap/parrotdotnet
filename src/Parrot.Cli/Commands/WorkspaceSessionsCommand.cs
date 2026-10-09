using System.Globalization;
using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli.Commands;

internal sealed class WorkspaceSessionsCommand(
    ITerminalSessionController navigation,
    ISlashSession session,
    ISlashActivity activity,
    ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/workspace-sessions";

    public string Summary => "Select a session from the current workspace";

    public async Task Run(string arguments, CancellationToken cancellationToken)
    {
        try
        {
            var listed = await dialog.Load("Loading workspace sessions…", navigation.List, cancellationToken)
                .ConfigureAwait(false);
            if (listed.Sessions.Count == 0)
            {
                await dialog.Show(["No sessions in the current workspace."], cancellationToken).ConfigureAwait(false);
                return;
            }

            var options = listed.Sessions
                .Select(item => (Session: item, Created: ParseCreationTime(item.CreatedAt)))
                .OrderByDescending(item => item.Created.HasValue)
                .ThenByDescending(item => item.Created)
                .ThenBy(item => item.Session.UserSessionId, StringComparer.Ordinal)
                .Select(item =>
                {
                    var marker = string.Equals(item.Session.UserSessionId, session.Id, StringComparison.Ordinal) ? "*" : " ";
                    var name = item.Session.RootAgentName.Length == 0 ? "<unnamed>" : item.Session.RootAgentName;
                    var label = $"{marker} {name}  {item.Session.UserSessionId}";
                    var created = item.Created is { } instant
                        ? instant.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
                        : "unknown creation time";
                    return new SlashDialogOption(item.Session.UserSessionId, label, $"{State(item.Session.State)}  {created}");
                })
                .ToArray();
            var selected = await dialog.Select("Workspace sessions", options, cancellationToken).ConfigureAwait(false);
            if (selected is null || string.Equals(selected.Id, session.Id, StringComparison.Ordinal))
            {
                return;
            }

            if (listed.Sessions.Single(item => item.UserSessionId == selected.Id).State == SessionState.Corrupt)
            {
                await dialog.ShowError("session metadata is corrupt", cancellationToken).ConfigureAwait(false);
                return;
            }

            await activity.WaitUntilIdle(cancellationToken).ConfigureAwait(false);
            _ = await dialog.Load(
                "Loading selected session…",
                async token =>
                {
                    await navigation.Load(selected.Id, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException failure) when (!cancellationToken.IsCancellationRequested && failure.StatusCode != StatusCode.Cancelled)
        {
            await dialog.ShowError(
                failure.StatusCode == StatusCode.Unimplemented
                    ? "workspace session selection is unavailable on this server"
                    : failure.Status.Detail,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested
            && failure is InvalidOperationException or IOException or TimeoutException)
        {
            await dialog.ShowError(failure.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private static DateTimeOffset? ParseCreationTime(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created)
            ? created
            : null;

    private static string State(SessionState state) => state switch
    {
        SessionState.Active => "active",
        SessionState.Inactive => "inactive",
        SessionState.Corrupt => "corrupt",
        _ => "unknown",
    };
}
