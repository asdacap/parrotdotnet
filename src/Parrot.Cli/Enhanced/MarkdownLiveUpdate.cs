namespace Parrot.Cli.Enhanced;

internal readonly record struct MarkdownLiveUpdate(
    IReadOnlyList<string> Scrollback,
    IReadOnlyList<string> Preview);
