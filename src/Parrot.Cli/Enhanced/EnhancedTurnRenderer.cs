using Grpc.Core;
using Parrot.Config;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedTurnRenderer(
    ITerminal terminal,
    Configuration configuration)
{
    internal Task<bool> RenderTurn(IAsyncStreamReader<Event> stream, CancellationToken cancellationToken) =>
        RenderTurn(stream, null, true, null, null, null, new ForegroundTurn(), cancellationToken);

    internal Task<bool> RenderTurn(
        IAsyncStreamReader<Event> stream,
        Func<Event, CancellationToken, Task> beforeRender,
        CancellationToken cancellationToken) =>
        RenderTurn(stream, beforeRender, true, null, null, null, new ForegroundTurn(), cancellationToken);

    internal Task<bool> RenderSessionTurn(
        IAsyncStreamReader<Event> stream,
        Func<Event, CancellationToken, Task> beforeRender,
        Func<Event, CancellationToken, Task> afterRender,
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
        ForegroundTurn foreground,
        CancellationToken cancellationToken) =>
        RenderTurn(
            stream,
            beforeRender,
            false,
            afterRender,
            draw,
            commit,
            foreground,
            cancellationToken);

    private static Task NoLiveDraw(IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private async Task<bool> RenderTurn(
        IAsyncStreamReader<Event> stream,
        Func<Event, CancellationToken, Task>? beforeRender,
        bool renderActivityEvents,
        Func<Event, CancellationToken, Task>? afterRender,
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task>? draw,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task>? commit,
        ForegroundTurn foreground,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        async Task CommitStandalone(
            IScrollbackItem scrollback,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var context = new ScrollbackRenderContext(
                Math.Max(1, terminal.GetColumns()),
                new TerminalPalette(terminal.Color),
                configuration.InlineDiff);
            foreach (var line in scrollback.Render(context))
            {
                await terminal.Output.WriteAsync(line.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                await terminal.Output.WriteAsync("\r\n".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            }

            await terminal.Output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var view = new EnhancedTurnView(
            draw ?? NoLiveDraw,
            commit ?? CommitStandalone,
            terminal.Error,
            terminal.GetColumns,
            renderActivityEvents,
            terminal.Color,
            foreground);

        try
        {
            while (await stream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                if (beforeRender is { } before)
                {
                    await before(stream.Current, cancellationToken).ConfigureAwait(false);
                }

                await view.Prepare(stream.Current, cancellationToken).ConfigureAwait(false);
                var completed = await view.Render(stream.Current, cancellationToken).ConfigureAwait(false);
                if (afterRender is { } after)
                {
                    await after(stream.Current, cancellationToken).ConfigureAwait(false);
                }

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
        catch
        {
            await view.Cancel(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
