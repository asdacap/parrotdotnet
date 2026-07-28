namespace Parrot.Cli.Enhanced;

internal sealed class StreamingScrollbackSequenceValue
{
    public IScrollbackItem Append(IReadOnlyList<string> lines) => new StreamingScrollbackValue(this, lines, false);

    public IScrollbackItem Complete(IReadOnlyList<string> lines) => new StreamingScrollbackValue(this, lines, true);
}
