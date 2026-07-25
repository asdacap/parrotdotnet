namespace Parrot.Cli.Enhanced;

internal readonly record struct LiveTerminalStreamMessage(string Id, string Prefix, string Text);
