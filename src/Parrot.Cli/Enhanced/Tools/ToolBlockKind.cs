namespace Parrot.Cli.Enhanced.Tools;

internal enum ToolBlockKind
{
    None,
    Text,
    Output,
    Status,
    Diff,
    Code,
    Queue,
    CompletedInput,
    Error,
}
