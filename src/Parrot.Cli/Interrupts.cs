namespace Parrot.Cli;

// Routes Ctrl-C. Nothing subscribes to a .NET event here: there is at most one
// listener, because the signal has one meaning at a time, and a list of
// handlers would leave "did anyone take it" to a convention.
//
// With no listener -- every command but the interactive session -- the signal
// stops the process, which is what it did before anything could claim it.
internal sealed class Interrupts(CancellationTokenSource stopping)
{
    private IInterruptListener? _listener;

    public void Install(IInterruptListener listener) => _listener = listener;

    public void Remove() => _listener = null;

    public void Signal()
    {
        if (_listener?.Interrupted() != true)
        {
            stopping.Cancel();
        }
    }
}
