namespace Parrot.Cli.Enhanced.Tools;

internal enum ToolBlockKind
{
    None,
    Text,
    Output,
    Diff,
    Code,
    Todos,
    Queue,
    CompletedInput,
    Error,
}
