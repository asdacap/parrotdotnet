using System.Runtime.InteropServices;

namespace Parrot.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] arguments)
    {
        using var cancellation = new CancellationTokenSource();
        var interrupts = new Interrupts(cancellation);

        // PosixSignalRegistration rather than Console.CancelKeyPress: the latter
        // is a .NET event, and this also covers SIGTERM.
        //
        // SIGINT is offered to whoever is driving a session before it stops the
        // process: with a turn running, Ctrl-C means stop the turn. Nobody can
        // claim SIGTERM, which is not a keystroke and does mean stop.
        using var interrupt = Register(PosixSignal.SIGINT, interrupts.Signal);
        using var terminate = Register(PosixSignal.SIGTERM, cancellation.Cancel);
        using var composition = new CommandComposition(interrupts, Console.Out, Console.Error);

        return await composition.Dispatcher.Run(arguments, cancellation.Token);
    }

    // Action rather than a declared delegate type: PARROT0002 bans the latter,
    // and a local callback is what MIGRATION.md section 5 allows this for.
    private static PosixSignalRegistration Register(PosixSignal signal, Action handle) =>
        PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            handle();
        });
}
