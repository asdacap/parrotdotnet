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

        // Listen before sending: a stream opened after the turn starts would
        // miss its opening events.
        using var call = client.Listen(
            new ListenRequest { SessionId = session.Id }, cancellationToken: cancellationToken);

        _ = await client.SendMessageAsync(
            new SendMessageRequest { SessionId = session.Id, Text = prompt },
            cancellationToken: cancellationToken);
        var failed = false;

        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            var published = call.ResponseStream.Current;

            switch (published.PayloadCase)
            {
                case Event.PayloadOneofCase.TextChunk:
                    await output.WriteAsync(published.TextChunk.Fragment.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.TurnEnded:
                    var ended = published.TurnEnded;
                    await output.WriteLineAsync().ConfigureAwait(false);
                    await output.WriteLineAsync(
                        $"  {Summarise(ended)}".AsMemory(), cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.TurnFailed:
                    failed = true;
                    await error.WriteLineAsync(
                        $"parrot: {published.TurnFailed.Message}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    break;
            }
        }

        return failed ? CommandDispatcher.ExitFailure : CommandDispatcher.ExitSuccess;
    }

    private static string Summarise(TurnEnded ended) =>
        ended.FinishReason == "length" && ended.OutputTokens > 0
            ? "turn ended: the token budget was spent before any content"
            : $"turn ended ({ended.FinishReason}, {ended.InputTokens} in / {ended.OutputTokens} out)";
}
