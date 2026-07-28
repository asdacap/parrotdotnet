namespace Parrot.Cli.Enhanced;

internal interface IScrollbackItem
{
    bool IsCompleted { get; }

    bool Continues(IScrollbackItem previous);

    IReadOnlyList<string> Render(ScrollbackRenderContext context);
}
