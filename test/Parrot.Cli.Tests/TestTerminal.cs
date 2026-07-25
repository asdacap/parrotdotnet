using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class TestTerminal(
    TextReader input,
    TextWriter output,
    TextWriter error,
    Func<IRawTerminal?> openRaw) : ITerminal
{
    public TextReader Input { get; } = input;

    public TextWriter Output { get; } = output;

    public TextWriter Error { get; } = error;

    public bool InputRedirected => false;

    public bool Color => false;

    public int GetColumns() => 80;

    public IRawTerminal? OpenRaw() => openRaw();
}
