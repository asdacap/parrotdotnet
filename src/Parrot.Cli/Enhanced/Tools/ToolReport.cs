namespace Parrot.Cli.Enhanced.Tools;

internal readonly record struct ToolReport(
    string Label,
    ToolTerminalStatus? Status,
    ToolBlock Block,
    ToolPresentationMetadata Metadata)
{
    public bool IsTerminal => Status.HasValue;

    public static ToolReport DescribeLive(
        string label,
        ToolBlock block,
        ToolPresentationMetadata metadata) => new(label, null, block, metadata);

    public static ToolReport DescribeTerminal(
        string label,
        ToolBlock block,
        ToolTerminalStatus status,
        ToolPresentationMetadata metadata) => new(label, status, block, metadata);
}
