namespace Parrot.Process;

internal interface IProcessSignalTarget : IDisposable
{
    void Send(ProcessSignal signal);
}
