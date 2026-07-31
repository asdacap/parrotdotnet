using Grpc.Core;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Config;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedTurnRenderer(
    ITerminal terminal,
    Configuration configuration,
    ToolPresenterRegistry toolPresenters)
{
    internal Task<bool> RenderTurn(IAsyncStreamReader<Event> stream, CancellationToken cancellationToken) =>
        RenderTurn(stream, null, true, null, null, null, new ForegroundTurn(), cancellationToken);

    internal Task<bool> RenderTurn(
        IAsyncStreamReader<Event> stream,
        Func<Event, CancellationToken, Task> beforeRender,
        CancellationToken cancellationToken) =>
        RenderTurn(stream, beforeRender, true, null, null, null, new ForegroundTurn(), cancellationToken);

    internal async Task<bool> RenderRaw(
        IAsyncStreamReader<Event> stream,
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
        Func<string, CancellationToken, Task> updateMainAgentActivity,
        Func<Event, CancellationToken, Task> observe,
        Func<CancellationToken, Task> finishTurn,
        Func<CancellationToken, Task> ready,
        Func<Task> stopSpinner,
        Func<bool> invalidate,
        Func<PlanCompleted, CancellationToken, Task> completePlan,
        bool exitOnFirstCompletion,
        CancellationToken cancellationToken)
    {
        var foreground = new ForegroundTurn();
        using var activity = new RawActivityView(
            draw,
            commit,
            toolPresenters,
            updateMainAgentActivity,
            invalidate);
        using var animating = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var animation = activity.Run(animating.Token);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var failed = false;
                PlanCompleted? plan = null;

                async Task BeforeRender(Event published, CancellationToken token)
                {
                    foreground.Observe(published);
                    if (published.PayloadCase == Event.PayloadOneofCase.TurnFailed
                        && foreground.IsTerminal(published))
                    {
                        failed = true;
                    }
                    else if (published.PayloadCase == Event.PayloadOneofCase.PlanCompleted
                             && foreground.IsMain(published.AgentSessionId))
                    {
                        plan = published.PlanCompleted;
                    }

                    await observe(published, token).ConfigureAwait(false);
                    await stopSpinner().ConfigureAwait(false);
                    await activity.Prepare(published, token).ConfigureAwait(false);
                }

                var completed = await RenderTurn(
                    stream,
                    BeforeRender,
                    false,
                    async (published, eventToken) =>
                    {
                        await activity.Render(published, eventToken).ConfigureAwait(false);

                        // Events update cached state or commit scrollback. This delayed invalidation
                        // coalesces event bursts; user interaction can still redraw immediately.
                        _ = invalidate();
                    },
                    activity.ReplaceContent,
                    activity.CommitContent,
                    foreground,
                    cancellationToken).ConfigureAwait(false);

                if (!completed && !failed)
                {
                    return false;
                }

                await finishTurn(cancellationToken).ConfigureAwait(false);
                if (plan is not null)
                {
                    if (plan.Markdown.Length > 0)
                    {
                        await activity.CommitContent(
                            new MarkdownScrollbackValue(plan.Markdown),
                            [],
                            cancellationToken).ConfigureAwait(false);
                    }

                    await completePlan(plan, cancellationToken).ConfigureAwait(false);
                }

                if (exitOnFirstCompletion)
                {
                    return completed;
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    await ready(cancellationToken).ConfigureAwait(false);
                }
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            await animating.CancelAsync().ConfigureAwait(false);
            await animation.ConfigureAwait(false);
        }
    }

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
