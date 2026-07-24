using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli;

// The one seam between the two clients. The loop and the slash commands are
// shared input plumbing; rendering is not, so that BasicCli stays a pure test
// of the event contract and EnhancedCli cannot lend it a compensating helper.
internal interface ITurnRenderer
{
    Task<bool> RenderTurn(
        IAsyncStreamReader<Event> stream, TextWriter output, TextWriter error, CancellationToken cancellationToken);
}
