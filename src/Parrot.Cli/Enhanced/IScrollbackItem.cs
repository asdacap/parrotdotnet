namespace Parrot.Cli.Enhanced;

internal interface IScrollbackItem
{
    bool IsCompleted { get; }

    ScrollbackLayout Layout => ScrollbackLayout.Compact;

    bool StartsLayout => true;

    bool EndsLayout => true;

    bool Continues(IScrollbackItem previous);

    IReadOnlyList<string> Render(ScrollbackRenderContext context);
}
