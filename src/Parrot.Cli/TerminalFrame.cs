namespace Parrot.Cli;

internal readonly record struct TerminalFrame(
    IReadOnlyList<string> Rows,
    SpinnerValue? Spinner,
    ModelineValue Modeline,
    PromptValue Prompt);
