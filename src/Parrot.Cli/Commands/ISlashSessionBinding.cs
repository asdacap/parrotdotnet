using Parrot.Protocol;

namespace Parrot.Cli.Commands;

internal interface ISlashSessionBinding
{
    Task Replace(UserSession session, CancellationToken cancellationToken);
}
