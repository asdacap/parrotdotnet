namespace Parrot.Cli;

// Who wants first refusal on Ctrl-C.
//
// It exists because the signal means two different things: with a turn running
// it means "stop that", and with nothing running it means "stop parrot". Only
// whoever is driving a session knows which, so the signal is offered rather
// than acted on.
internal interface IInterruptListener
{
    // True when the signal was taken. False hands it back, and the process
    // stops -- which is what makes a second Ctrl-C an exit.
    //
    // Called on the signal handler's thread, so it must not block.
    bool Interrupted();
}
