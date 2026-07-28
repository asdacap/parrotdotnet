namespace Parrot.Cli.Enhanced.Tools;

internal readonly record struct ToolPresentationMetadata(
    ToolPresentationStyle Style,
    string SuccessIcon,
    bool LiveOnly,
    bool TerminalOnly,
    bool Modeline,
    IReadOnlyList<string> RedactedInputFields)
{
    public static ToolPresentationMetadata Default { get; } =
        new(ToolPresentationStyle.Default, string.Empty, false, false, false, []);
}
