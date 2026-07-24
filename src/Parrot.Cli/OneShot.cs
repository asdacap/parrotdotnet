using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

// One prompt, one reply, then exit. Kept because pipes and scripts depend on
// it, and because it is how a live turn gets verified without a terminal.
internal static class OneShot
{
    public static async Task<int> Run(
        GeneratedParrot.ParrotClient client,
        string model,
        string prompt,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var session = await client.CreateSessionAsync(
            new CreateSessionRequest { Model = model }, cancellationToken: cancellationToken);

        // The stream is indefinite, so this call cancels once its one turn is
        // done rather than waiting for the server to stop.
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Listen before sending: a stream opened after the turn starts would
        // miss its opening events.
        using var call = client.Listen(
            new ListenRequest { UserSessionId = session.Id }, cancellationToken: listening.Token);

        _ = await client.SendMessageAsync(
            new SendMessageRequest { UserSessionId = session.Id, Text = prompt },
            cancellationToken: cancellationToken);

        var completed = await BasicCli
            .RenderTurn(call.ResponseStream, output, error, listening.Token)
            .ConfigureAwait(false);

        await listening.CancelAsync().ConfigureAwait(false);

        return completed ? CommandDispatcher.ExitSuccess : CommandDispatcher.ExitFailure;
    }
}
