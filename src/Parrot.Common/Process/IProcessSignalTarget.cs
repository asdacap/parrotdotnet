namespace Parrot.Process;

/// <summary>Owns the signaling handle or identity for one tracked top-level process, not its process tree.</summary>
internal interface IProcessSignalTarget : IDisposable
{
    /// <summary>Sends the signal to the tracked process, throwing if the host cannot deliver it.</summary>
    void Send(ProcessSignal signal);
}
