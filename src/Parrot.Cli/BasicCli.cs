using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli;

// A switch over the payload and a WriteLine. No model of the conversation
// beyond what it has printed, and no helper. If this file ever needs one, the
// event is underspecified -- fix the event, not the CLI.
internal static class BasicCli
{
    // Renders one turn and returns, leaving the stream open. That is what lets
    // a session share a single Listen call across every turn: the stream is
    // indefinite by design, and only the client decides when it is done.
    public static async Task<bool> RenderTurn(
        IAsyncStreamReader<Event> stream,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        while (await MoveNext(stream, cancellationToken).ConfigureAwait(false))
        {
            var published = stream.Current;

            switch (published.PayloadCase)
            {
                case Event.PayloadOneofCase.TextChunk:
                    await output.WriteAsync(published.TextChunk.Fragment.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.TurnEnded:
                    await output.WriteLineAsync().ConfigureAwait(false);
                    await output.WriteLineAsync(
                        $"  {Summarise(published.TurnEnded)}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    return true;

                case Event.PayloadOneofCase.TurnFailed:
                    await error.WriteLineAsync(
                        $"parrot: {published.TurnFailed.Message}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    return false;

                default:
                    break;
            }
        }

        return false;
    }

    // A cancelled stream is an ending, not a failure: cancelling is how a
    // caller says it has heard enough.
    private static async Task<bool> MoveNext(IAsyncStreamReader<Event> stream, CancellationToken cancellationToken)
    {
        try
        {
            return await stream.MoveNext(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string Summarise(TurnEnded ended) =>
        ended.FinishReason == "length" && ended.OutputTokens > 0
            ? "turn ended: the token budget was spent before any content"
            : $"turn ended ({ended.FinishReason}, {ended.InputTokens} in / {ended.OutputTokens} out)";
}
