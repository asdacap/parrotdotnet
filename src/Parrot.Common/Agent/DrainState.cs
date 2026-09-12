namespace Parrot.Agent;

// Which of the three things a session is doing. At most one drain owns a
// session in one process (principle 2), so this is the state of that one drain
// and not a count of anything.
internal enum DrainState
{
    // Nothing promoted and nothing pending. A prompt starts a drain from here.
    Idle,

    // A drain owns the session: it is promoting input, calling the provider, or
    // running tools.
    Running,

    // Cancellation has been asked for and the drain has not finished unwinding.
    // It exists because a turn is a cancellable boundary that must still settle
    // -- every tool call finishes before the next turn (principle 6) -- so
    // stopping one is not the same as having stopped it.
    Interrupting,
}
