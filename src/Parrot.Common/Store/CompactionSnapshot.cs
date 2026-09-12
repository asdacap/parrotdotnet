namespace Parrot.Store;

internal sealed record CompactionSnapshot(string Summary, long Watermark);
