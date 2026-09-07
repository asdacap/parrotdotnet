using Parrot.Protocol;

namespace Parrot.Cli.Commands;

internal sealed class CliSlashSessionBinding(Func<UserSession, CancellationToken, Task> replace) : ISlashSessionBinding
{
    public Task Replace(UserSession session, CancellationToken cancellationToken) => replace(session, cancellationToken);
}
