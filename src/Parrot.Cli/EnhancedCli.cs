using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli;

// The richer client. It reads the typed payload rather than a rendered line:
// reasoning is dimmed, tool calls are announced, the assistant text is plain,
// and the turn summary is set apart. No alternate screen -- output scrolls, so
// the terminal's scrollback is the history. It shares no rendering with
// BasicCli; both only implement ITurnRenderer and use the generated client.
internal sealed class EnhancedCli : ITurnRenderer
{
    private const string Dim = "\u001b[2m";
    private const string Cyan = "\u001b[36m";
    private const string Green = "\u001b[32m";
    private const string Red = "\u001b[31m";
    private const string Reset = "\u001b[0m";

    public async Task<bool> RenderTurn(
        IAsyncStreamReader<Event> stream, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var reasoning = false;

        // Whether the prompt this call is rendering has started. Before it has,
        // an admission is the prompt the user just typed and is already on
        // screen; after it, an admission is one they typed over the top of a
        // turn, and saying so is the only sign it was taken.
        var started = false;

        while (await MoveNext(stream, cancellationToken).ConfigureAwait(false))
        {
            var published = stream.Current;

            // Reasoning prints dim; a switch away from it closes the dim run.
            if (reasoning && published.PayloadCase != Event.PayloadOneofCase.ReasoningChunk)
            {
                await output.WriteAsync(Reset.AsMemory(), cancellationToken).ConfigureAwait(false);
                reasoning = false;
            }

            switch (published.PayloadCase)
            {
                case Event.PayloadOneofCase.TurnStarted:
                    started = true;
                    break;

                case Event.PayloadOneofCase.InputAdmitted when started:
                    await output.WriteLineAsync().ConfigureAwait(false);
                    await output.WriteLineAsync(
                        $"{Dim}  queued: {published.InputAdmitted.Content}{Reset}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ReasoningChunk:
                    if (!reasoning)
                    {
                        await output.WriteAsync(Dim.AsMemory(), cancellationToken).ConfigureAwait(false);
                        reasoning = true;
                    }

                    await output.WriteAsync(published.ReasoningChunk.Fragment.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.TextChunk:
                    await output.WriteAsync(published.TextChunk.Fragment.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ToolCallChunk when published.ToolCallChunk.ToolName.Length > 0:
                    await output.WriteLineAsync(
                        $"{Cyan}  * {published.ToolCallChunk.ToolName}{Reset}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.TurnEnded:
                    await output.WriteLineAsync().ConfigureAwait(false);
                    await output.WriteLineAsync(
                        $"{Green}  {Summarise(published.TurnEnded)}{Reset}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    return true;

                case Event.PayloadOneofCase.TurnFailed:
                    await error.WriteLineAsync(
                        $"{Red}  {published.TurnFailed.Message}{Reset}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    return false;

                default:
                    break;
            }
        }

        return false;
    }

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
        $"{ended.FinishReason} - {ended.InputTokens} in / {ended.OutputTokens} out";
}
