namespace Parrot.Cli.Enhanced.Tools;

internal readonly record struct ToolCallPresentation(
    string Owner,
    string ToolName,
    string ArgumentsJson);
