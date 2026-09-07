namespace Parrot.Cli;

internal sealed class CliInterruptListener(Func<bool> interrupt) : IInterruptListener
{
    public static IInterruptListener Create(Func<bool> interrupt) => new CliInterruptListener(interrupt);

    public bool Interrupted() => interrupt();
}
