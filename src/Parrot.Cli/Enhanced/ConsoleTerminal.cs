namespace Parrot.Cli.Enhanced;

internal sealed class ConsoleTerminal(TextWriter output, TextWriter error) : ITerminal
{
    public TextReader Input => Console.In;

    public TextWriter Output { get; } = output;

    public TextWriter Error { get; } = error;

    public bool InputRedirected => Console.IsInputRedirected;

    public bool Color => Environment.GetEnvironmentVariable("NO_COLOR") is null;

    public int GetColumns() => Console.WindowWidth;

    public IRawTerminal? OpenRaw() =>
        string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal)
            ? null
            : UnixRawTerminal.Open();
}
