using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli;

internal sealed class EnhancedCli(Func<int> columns) : ITurnRenderer
{
    public EnhancedCli()
        : this(static () => Console.WindowWidth)
    {
    }

    public async Task<bool> RenderTurn(
        IAsyncStreamReader<Event> stream, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var view = new TurnView(output, error, columns);

        try
        {
            while (await stream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                var completed = await view.Render(stream.Current, cancellationToken).ConfigureAwait(false);
                if (completed is not null)
                {
                    return completed.Value;
                }
            }

            await view.End(cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            await view.Cancel(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    private sealed class TurnView(TextWriter output, TextWriter error, Func<int> columns)
    {
        private const string Dim = "\u001b[2m";
        private const string Cyan = "\u001b[36m";
        private const string Green = "\u001b[32m";
        private const string Red = "\u001b[31m";
        private const string Reset = "\u001b[0m";

        private readonly LiveTerminalRenderer _live = new(output, columns);
        private bool _reasoning;
        private bool _reasoningEndsLine;
        private bool _started;
        private bool _textActive;
        private int _textSegment;

        private string TextId => $"assistant-{_textSegment}";

        public async Task<bool?> Render(Event published, CancellationToken cancellationToken)
        {
            if (_textActive && published.PayloadCase != Event.PayloadOneofCase.TextChunk)
            {
                await CommitText(cancellationToken).ConfigureAwait(false);
            }

            if (_reasoning && published.PayloadCase != Event.PayloadOneofCase.ReasoningChunk)
            {
                await EndReasoning(cancellationToken).ConfigureAwait(false);
            }

            switch (published.PayloadCase)
            {
                case Event.PayloadOneofCase.TurnStarted:
                    _started = true;
                    break;

                case Event.PayloadOneofCase.InputAdmitted when _started:
                    await output.WriteLineAsync(
                        $"{Dim}  queued: {TerminalText.Sanitize(published.InputAdmitted.Content)}{Reset}".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ReasoningChunk:
                    await RenderReasoning(published.ReasoningChunk.Fragment, cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.TextChunk:
                    _textActive = true;
                    await _live.Append(
                        new LiveTerminalStreamMessage(TextId, string.Empty, published.TextChunk.Fragment),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ToolCallChunk when published.ToolCallChunk.ToolName.Length > 0:
                    await output.WriteLineAsync(
                        $"{Cyan}  * {TerminalText.Sanitize(published.ToolCallChunk.ToolName)}{Reset}".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.TurnEnded:
                    await output.WriteLineAsync(
                        $"{Green}  {Summarise(published.TurnEnded)}{Reset}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    return true;

                case Event.PayloadOneofCase.TurnFailed:
                    await error.WriteLineAsync(
                        $"{Red}  {TerminalText.Sanitize(published.TurnFailed.Message)}{Reset}".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    return false;

                default:
                    break;
            }

            return null;
        }

        public async Task End(CancellationToken cancellationToken)
        {
            if (_textActive)
            {
                await CommitText(cancellationToken).ConfigureAwait(false);
            }

            if (_reasoning)
            {
                await EndReasoning(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task Cancel(CancellationToken cancellationToken)
        {
            if (_textActive)
            {
                await _live.Clear(cancellationToken).ConfigureAwait(false);
            }

            if (_reasoning)
            {
                await output.WriteAsync(Reset.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        private static string Summarise(TurnEnded ended) =>
            $"{TerminalText.Sanitize(ended.FinishReason)} - {ended.InputTokens} in / {ended.OutputTokens} out";

        private async Task CommitText(CancellationToken cancellationToken)
        {
            await _live.Commit(cancellationToken).ConfigureAwait(false);
            _textActive = false;
            _textSegment++;
        }

        private async Task RenderReasoning(string fragment, CancellationToken cancellationToken)
        {
            if (!_reasoning)
            {
                await output.WriteAsync(Dim.AsMemory(), cancellationToken).ConfigureAwait(false);
                _reasoning = true;
            }

            var clean = TerminalText.Sanitize(fragment);
            await output.WriteAsync(clean.AsMemory(), cancellationToken).ConfigureAwait(false);
            _reasoningEndsLine = clean.EndsWith('\n');
        }

        private async Task EndReasoning(CancellationToken cancellationToken)
        {
            await output.WriteAsync(Reset.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (!_reasoningEndsLine)
            {
                await output.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
            }

            _reasoning = false;
            _reasoningEndsLine = false;
        }
    }
}
