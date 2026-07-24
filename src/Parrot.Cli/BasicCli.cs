using Parrot.Protocol;

namespace Parrot.Cli;

// A switch over kind and a WriteLine of text. No model of the conversation
// beyond what it has printed, and no helper. If this file ever needs one, the
// event is underspecified -- fix the event, not the CLI.
internal static class BasicCli
{
    public static async Task<int> Render(
        Parrot.Protocol.ParrotAgent.ParrotAgentClient client,
        ChatRequest request,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var call = client.Chat(request, cancellationToken: cancellationToken);
        var failed = false;

        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            var published = call.ResponseStream.Current;

            switch (published.Kind)
            {
                case EventKind.Text:
                    await output.WriteAsync(published.Text.AsMemory(), cancellationToken).ConfigureAwait(false);
                    break;

                case EventKind.Error:
                    failed = true;
                    await error.WriteLineAsync($"parrot: {published.Text}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case EventKind.TurnEnd:
                    await output.WriteLineAsync().ConfigureAwait(false);
                    await output.WriteLineAsync($"  {published.Text}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    break;
            }
        }

        return failed ? CommandDispatcher.ExitFailure : CommandDispatcher.ExitSuccess;
    }
}
