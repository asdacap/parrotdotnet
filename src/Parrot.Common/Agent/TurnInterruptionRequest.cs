namespace Parrot.Agent;

/// <summary>Tracks whether the model requested its turn to be interrupted via agent_interrupt.</summary>
internal sealed class TurnInterruptionRequest
{
    private readonly Lock _gate = new();
    private bool _requested;

    /// <summary>Marks the current turn as interruption requested.</summary>
    public void Request()
    {
        lock (_gate)
        {
            _requested = true;
        }
    }

    /// <summary>Returns whether an interruption was requested and clears the marker.</summary>
    public bool Consume()
    {
        lock (_gate)
        {
            var requested = _requested;
            _requested = false;
            return requested;
        }
    }
}
