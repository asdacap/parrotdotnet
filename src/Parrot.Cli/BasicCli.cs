using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli;

// A switch over the payload and a WriteLine. No model of the conversation
// beyond what it has printed, and no helper. If this ever needs one, the event
// is underspecified -- fix the event, not the CLI. It shares no rendering with
// EnhancedCli, only the ITurnRenderer seam and the generated client.
internal sealed class BasicCli : ITurnRenderer
{
    public async Task<bool> RenderTurn(
        IAsyncStreamReader<Event> stream, TextWriter output, TextWriter error, CancellationToken cancellationToken)
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
                        $"  {Summarise(published.TurnEnded)}".AsMemory(), cancellationToken).ConfigureAwait(false);
                    return true;

                case Event.PayloadOneofCase.TurnFailed:
                    await error.WriteLineAsync(
                        $"parrot: {published.TurnFailed.Message}".AsMemory(), cancellationToken).ConfigureAwait(false);
                    return false;

                default:
                    break;
            }
        }

        return false;
    }

    // A cancelled stream is an ending, not a failure.
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
