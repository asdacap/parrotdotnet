using Parrot.Protocol;

namespace Parrot.Cli;

/// <summary>Lists workspace conversations and changes the terminal's current conversation without creating one.</summary>
internal interface ITerminalSessionController
{
    Task<ListSessionsResponse> List(CancellationToken cancellationToken);

    /// <summary>Loads the exact selected conversation, retaining the previous binding if switching fails.</summary>
    Task Load(string userSessionId, CancellationToken cancellationToken);
}
