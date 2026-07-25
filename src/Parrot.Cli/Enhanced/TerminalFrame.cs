namespace Parrot.Cli.Enhanced;

internal readonly record struct TerminalFrame(
    IReadOnlyList<string> Rows,
    SpinnerValue? Spinner,
    ModelineValue Modeline,
    PromptValue Prompt);
