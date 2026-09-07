using Parrot.Protocol;

namespace Parrot.Cli.Commands;

/// <summary>Connects session-changing commands to the active CLI session.</summary>
internal interface ISlashSessionBinding
{
    /// <summary>Rebinds the CLI to the supplied session before the command continues.</summary>
    Task Replace(UserSession session, CancellationToken cancellationToken);
}
