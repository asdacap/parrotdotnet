using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedTurnRenderer(ITerminal terminal)
{
    internal async Task<bool> RenderSessionTurn(
        IAsyncStreamReader<Event> stream,
        Func<Event, CancellationToken, Task> beforeRender,
        Func<Event, CancellationToken, Task> afterRender,
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
        ForegroundTurn foreground,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var view = new EnhancedTurnView(
            draw,
            commit,
            terminal.Error,
            terminal.GetColumns,
            terminal.Color,
            foreground);

        try
        {
            while (await stream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                await beforeRender(stream.Current, cancellationToken).ConfigureAwait(false);
                await view.Prepare(stream.Current, cancellationToken).ConfigureAwait(false);
                var completed = await view.Render(stream.Current, cancellationToken).ConfigureAwait(false);
                await afterRender(stream.Current, cancellationToken).ConfigureAwait(false);
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
