using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class RawActivityRecorder : IAsyncDisposable
{
    public static readonly Func<TimeSpan, CancellationToken, Task> QuietPeriodDelay =
        static (quietPeriod, cancellationToken) => Task.Delay(quietPeriod, cancellationToken);

    private readonly LiveBufferRenderContext _liveContext;
    private readonly ScrollbackRenderContext _scrollbackContext;

    public RawActivityRecorder(
        int columns,
        ToolPresenterRegistry presenters,
        Func<TimeSpan, CancellationToken, Task> progressDelay)
    {
        _liveContext = new LiveBufferRenderContext(columns, new TerminalPalette(false));
        _scrollbackContext = new ScrollbackRenderContext(columns, _liveContext.Palette);
        View = new RawActivityView(
            Draw,
            Commit,
            static cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            progressDelay,
            presenters,
            static (_, _) => Task.CompletedTask);
    }

    public RawActivityView View { get; }

    public List<string> Drawn { get; } = [];

    public List<string> Committed { get; } = [];

    public string LastDrawn => Drawn.Count == 0 ? string.Empty : Drawn[^1];

    public string CommittedText => string.Join('|', Committed);

    public TaskCompletionSource CommitObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static string Render(IReadOnlyList<ILiveBufferItem> items, LiveBufferRenderContext context) =>
        string.Join('|', items.SelectMany(item => item.Render(context).Lines).Select(static line => line.Text));

    public ValueTask DisposeAsync() => View.DisposeAsync();

    private Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Drawn.Add(Render(items, _liveContext));
        return Task.CompletedTask;
    }

    private Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Committed.Add(string.Join('|', item.Render(_scrollbackContext)));
        _ = CommitObserved.TrySetResult();
        Drawn.Add(Render(items, _liveContext));
        return Task.CompletedTask;
    }
}
