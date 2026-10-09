using Parrot.Protocol;

namespace Parrot.Cli;

/// <summary>Lists workspace sessions and opens an exact existing session for a terminal.</summary>
internal interface ITerminalSessionNavigation : IDisposable
{
    Task<ListSessionsResponse> List(UserSession current, CancellationToken cancellationToken);

    /// <summary>Opens the selected session without creating a replacement conversation.</summary>
    Task<TerminalSessionTarget> Open(UserSession current, string userSessionId, CancellationToken cancellationToken);
}
