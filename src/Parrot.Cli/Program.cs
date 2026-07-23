using System.Runtime.InteropServices;

namespace Parrot.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] arguments)
    {
        using var cancellation = new CancellationTokenSource();

        // PosixSignalRegistration rather than Console.CancelKeyPress: the latter
        // is a .NET event, and this also covers SIGTERM.
        using var interrupt = Register(PosixSignal.SIGINT, cancellation);
        using var terminate = Register(PosixSignal.SIGTERM, cancellation);

        return await CommandDispatcher.Run(arguments, Console.Out, Console.Error, cancellation.Token);
    }

    private static PosixSignalRegistration Register(PosixSignal signal, CancellationTokenSource cancellation) =>
        PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
}
