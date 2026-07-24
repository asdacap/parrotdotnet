namespace Parrot.Tools;

// The mutable side: aware of which tools are configured. Its job is to hand out
// an immutable ToolSnapshot per turn.
internal sealed class ToolRegistry(IReadOnlyList<ITool> tools)
{
    public ToolSnapshot Snapshot() => new(tools);
}
