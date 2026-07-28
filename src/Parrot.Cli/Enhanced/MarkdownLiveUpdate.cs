namespace Parrot.Cli.Enhanced;

internal readonly record struct MarkdownLiveUpdate(
    IScrollbackItem? Scrollback,
    IReadOnlyList<string> Preview);
