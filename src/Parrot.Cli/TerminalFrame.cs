namespace Parrot.Cli;

internal readonly record struct TerminalFrame(
    IReadOnlyList<string> Rows,
    ModelineValue Modeline,
    PromptValue Prompt);
