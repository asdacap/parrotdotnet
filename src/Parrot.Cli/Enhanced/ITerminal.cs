namespace Parrot.Cli.Enhanced;

internal interface ITerminal
{
    TextReader Input { get; }

    TextWriter Output { get; }

    TextWriter Error { get; }

    bool InputRedirected { get; }

    bool Color { get; }

    int GetColumns();

    IRawTerminal? OpenRaw();
}
