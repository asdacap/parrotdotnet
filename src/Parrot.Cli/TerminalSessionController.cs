using Parrot.Cli.Commands;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class TerminalSessionController(
    ITerminalSessionNavigation navigation,
    TerminalSessionCallInvoker routing,
    ISlashSession session,
    UserSession initialSession,
    Func<UserSession, GeneratedParrot.ParrotClient, Action, CancellationToken, Task> replace) : ITerminalSessionController, IDisposable
{
    private readonly List<TerminalSessionTarget> _targets = [];
    private UserSession _current = initialSession;

    public Task<ListSessionsResponse> List(CancellationToken cancellationToken) =>
        navigation.List(_current, cancellationToken);

    public async Task Load(string userSessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(userSessionId);
        if (string.Equals(userSessionId, session.Id, StringComparison.Ordinal))
        {
            return;
        }

        var target = await navigation.Open(_current, userSessionId, cancellationToken).ConfigureAwait(false);
        var previous = routing.Target;
        try
        {
            await session.LoadExisting(
                target.Session,
                (commit, token) => replace(
                target.Session,
                target.Client,
                () =>
                {
                    routing.Target = target.Invoker;
                    commit();
                },
                token),
                cancellationToken).ConfigureAwait(false);
            _current = target.Session;
            _targets.Add(target);
        }
        catch
        {
            routing.Target = previous;
            target.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var target in _targets)
        {
            target.Dispose();
        }

        _targets.Clear();
    }
}
