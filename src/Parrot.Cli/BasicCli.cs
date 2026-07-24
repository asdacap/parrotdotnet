using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli;

// A switch over the payload and a WriteLine. No model of the conversation
// beyond what it has printed, and no helper. If this file ever needs one, the
// event is underspecified -- fix the event, not the CLI.
internal static class BasicCli
{
    public static async Task<int> Render(
        Parrot.Protocol.Parrot.ParrotClient client,
        string model,
        string prompt,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var session = await client.CreateSessionAsync(
            new CreateSessionRequest { Model = model }, cancellationToken: cancellationToken);

        // The stream is indefinite -- a subagent keeps publishing to it long
        // after a turn ends -- so this call decides when it has heard enough
        // and cancels, rather than waiting for the server to stop.
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Listen before sending: a stream opened after the turn starts would
        // miss its opening events.
        using var call = client.Listen(
            new ListenRequest { UserSessionId = session.Id }, cancellationToken: listening.Token);

        _ = await client.SendMessageAsync(
            new SendMessageRequest { UserSessionId = session.Id, Text = prompt },
            cancellationToken: cancellationToken);
        var failed = false;

        while (await MoveNext(call.ResponseStream, listening.Token).ConfigureAwait(false))
        {
            var published = call.ResponseStream.Current;

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
                    await listening.CancelAsync().ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.TurnFailed:
                    failed = true;
                    await error.WriteLineAsync(
                        $"parrot: {published.TurnFailed.Message}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    await listening.CancelAsync().ConfigureAwait(false);
                    break;

                default:
                    break;
            }
        }

        return failed ? CommandDispatcher.ExitFailure : CommandDispatcher.ExitSuccess;
    }

    // Cancelling the stream is how this call says it is done, so the
    // cancellation it caused is an ending rather than a failure.
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
