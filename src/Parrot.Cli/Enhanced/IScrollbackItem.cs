namespace Parrot.Cli.Enhanced;

/// <summary>A committed display item with completion and adjacent-block layout semantics.</summary>
internal interface IScrollbackItem
{
    bool IsCompleted { get; }

    ScrollbackLayout Layout => ScrollbackLayout.Compact;

    bool StartsLayout => true;

    bool EndsLayout => true;

    /// <summary>Gets the opaque streaming-group identity, or null when the item has no sequence.</summary>
    object? SequenceIdentity => null;

    /// <summary>Whether this item continues the previous adjacent item without starting a new layout group.</summary>
    bool Continues(IScrollbackItem previous);

    IReadOnlyList<string> Render(ScrollbackRenderContext context);
}
