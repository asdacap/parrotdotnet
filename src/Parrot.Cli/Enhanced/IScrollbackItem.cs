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

    /// <summary>Gets the value the next adjacent item inspects to decide whether it packs onto this item, or null when nothing packs onto it.</summary>
    object? PackingIdentity => null;

    /// <summary>Whether this item packs onto the previous adjacent item with the given packing identity, leaving no gap between them.</summary>
    bool Packs(object? previousPacking) => false;

    /// <summary>Whether this item continues the previous adjacent item without starting a new layout group.</summary>
    bool Continues(IScrollbackItem previous);

    IReadOnlyList<string> Render(ScrollbackRenderContext context);
}
