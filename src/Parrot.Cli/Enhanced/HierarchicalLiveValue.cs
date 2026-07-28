namespace Parrot.Cli.Enhanced;

internal sealed class HierarchicalLiveValue(
    ILiveBufferItem value,
    int depth,
    string? label,
    string owner,
    string? successfulIcon) : ILiveBufferItem
{
    public MultiLine Render(LiveBufferRenderContext context)
    {
        var prefixWidth = (depth * 2) + (label is null ? 0 : label.Length + 3);
        var rendered = value.Render(context with { Columns = Math.Max(1, context.Columns - prefixWidth) });
        var lines = rendered.Lines.Select((line, index) => line with
        {
            Text = HierarchicalActivityValue.Format(line.Text, depth, label, owner, successfulIcon, index == 0),
        }).ToList();
        return rendered with { Lines = lines };
    }
}
