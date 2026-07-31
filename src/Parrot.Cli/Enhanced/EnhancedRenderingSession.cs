using Grpc.Core;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedRenderingSession(
    EnhancedTurnRenderer turnRenderer,
    ToolPresenterRegistry toolPresenters,
    Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> replaceBody,
    Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commitBody,
    Func<string, CancellationToken, Task> updateMainAgentActivity,
    Func<Event, CancellationToken, Task> observeEvent,
    Func<CancellationToken, Task> finishTurn,
    Func<CancellationToken, Task> becomeReady,
    Func<Task> stopSpinner,
    Func<bool> invalidate,
    Func<PlanCompleted, CancellationToken, Task> completePlan,
    bool exitOnFirstCompletion)
{
    internal async Task<bool> Run(
        IAsyncStreamReader<Event> stream,
        CancellationToken cancellationToken)
    {
        var foreground = new ForegroundTurn();
        using var activity = new RawActivityView(
            replaceBody,
            commitBody,
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

                async Task Prepare(Event published, CancellationToken token)
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

                    await observeEvent(published, token).ConfigureAwait(false);
                    await stopSpinner().ConfigureAwait(false);
                    await activity.Prepare(published, token).ConfigureAwait(false);
                }

                async Task Render(Event published, CancellationToken token)
                {
                    await activity.Render(published, token).ConfigureAwait(false);
                    _ = invalidate();
                }

                var completed = await turnRenderer.RenderSessionTurn(
                    stream,
                    Prepare,
                    Render,
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
                    await becomeReady(cancellationToken).ConfigureAwait(false);
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
}
