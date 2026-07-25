namespace Parrot.Cli.Enhanced;

internal sealed class ConsoleTerminal(TextWriter output, TextWriter error, IRawTerminal rawTerminal) : ITerminal
{
    public TextReader Input => Console.In;

    public TextWriter Output { get; } = output;

    public TextWriter Error { get; } = error;

    public bool Color => Environment.GetEnvironmentVariable("NO_COLOR") is null;

    public int GetColumns() => Console.WindowWidth;

    public ValueTask<int> Read(byte[] buffer, CancellationToken cancellationToken) =>
        rawTerminal.Read(buffer, cancellationToken);
}
