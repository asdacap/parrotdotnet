namespace Parrot.Cli;

internal readonly record struct LiveTerminalStreamMessage(string Id, string Prefix, string Text);
