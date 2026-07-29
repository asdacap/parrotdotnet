namespace Parrot.Cli.Enhanced;

internal readonly record struct MarkdownLiveUpdate(
    IScrollbackItem? Scrollback,
    string Prefix,
    IReadOnlyList<string> Preview);
