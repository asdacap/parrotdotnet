using System.Threading.Channels;
using Grpc.Core;
using Parrot.Cli.Commands;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class PermissionInteractionPresenter(GeneratedParrot.ParrotClient client)
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMilliseconds(250);
    private readonly Channel<Request> _requests = Channel.CreateUnbounded<Request>();
    private readonly HashSet<string> _discovered = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private Session? _session;
    private long _generation;

    public Session Attach(string userSessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userSessionId);

        lock (_gate)
        {
            _generation++;
            _discovered.Clear();
            _session = new Session(userSessionId, _generation);
            return _session;
        }
    }

    public void Observe(Session session, PendingPermission pending)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pending);

        lock (_gate)
        {
            if (!Equals(_session, session) || !_discovered.Add(pending.Id))
            {
                return;
            }

            if (!_requests.Writer.TryWrite(new Request(session, pending.Clone())))
            {
                _ = _discovered.Remove(pending.Id);
            }
        }
    }

    public Request? Read()
    {
        while (_requests.Reader.TryRead(out var candidate))
        {
            lock (_gate)
            {
                if (Equals(_session, candidate.Session))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public ValueTask<bool> WaitToRead(CancellationToken cancellationToken) =>
        _requests.Reader.WaitToReadAsync(cancellationToken);

    public async Task Reconcile(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Session? session;
                lock (_gate)
                {
                    session = _session;
                }

                if (session is not null)
                {
                    var listed = await client.ListPendingPermissionsAsync(
                        new ListPendingPermissionsRequest { UserSessionId = session.UserSessionId },
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    foreach (var pending in listed.Permissions)
                    {
                        Observe(session, pending);
                    }
                }

                await Task.Delay(ReconciliationInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task Present(Request request, ISlashDialog dialog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dialog);

        while (true)
        {
            var pending = request.Pending;
            var options = pending.Choices
                .Select(choice => new SlashDialogOption(choice.Value, choice.Label, choice.Action.ToString()))
                .ToList();
            var selected = await dialog.Select(Describe(pending), options, cancellationToken).ConfigureAwait(false);
            var choice = selected is null
                ? pending.Choices.FirstOrDefault(candidate =>
                    candidate.Action == PermissionAction.Deny && !candidate.RequiresReason)
                : pending.Choices.FirstOrDefault(candidate =>
                    string.Equals(candidate.Value, selected.Id, StringComparison.Ordinal));

            if (choice is null)
            {
                await dialog.ShowError("The permission request has no plain reject choice.", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var reason = string.Empty;
            if (choice.RequiresReason)
            {
                var entered = await dialog.ReadText("Reason", cancellationToken).ConfigureAwait(false);
                if (entered is null)
                {
                    choice = pending.Choices.FirstOrDefault(candidate =>
                        candidate.Action == PermissionAction.Deny && !candidate.RequiresReason);
                    if (choice is null)
                    {
                        await dialog.ShowError("The permission request has no plain reject choice.", cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }
                }
                else
                {
                    reason = entered.Trim();
                    if (reason.Length == 0)
                    {
                        await dialog.ShowError("A reason is required.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
            }

            try
            {
                _ = await client.ReplyPermissionAsync(
                    new ReplyPermissionRequest
                    {
                        UserSessionId = request.Session.UserSessionId,
                        PermissionRequestId = pending.Id,
                        ChoiceValue = choice.Value,
                        Reason = reason,
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (RpcException failure) when (failure.StatusCode == StatusCode.NotFound)
            {
                return;
            }
            catch (RpcException failure) when (failure.StatusCode == StatusCode.InvalidArgument)
            {
                await dialog.ShowError(failure.Status.Detail, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string Describe(PendingPermission pending)
    {
        var targets = string.Join(", ", pending.Targets.Select(target =>
            $"{target.Scope.ToString().ToLowerInvariant()} {target.Kind.ToString().ToLowerInvariant()} {target.Path}"));
        return pending.Reason.Length == 0 ? targets : $"{targets} — {pending.Reason}";
    }

    internal sealed record Session(string UserSessionId, long Generation);

    internal sealed record Request(Session Session, PendingPermission Pending);
}
