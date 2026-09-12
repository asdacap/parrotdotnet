namespace Parrot.Agent;

internal sealed class RetainedAgentBudget(int capacity)
{
    private readonly Lock _gate = new();
    private int _pending;
    private int _retained;

    public RetainedAgentReservation Reserve()
    {
        lock (_gate)
        {
            if (_pending + _retained >= capacity)
            {
                throw new AgentRegistryException("subagent retention limit reached");
            }

            _pending++;
            return new RetainedAgentReservation(this);
        }
    }

    internal void Commit(RetainedAgentReservation reservation)
    {
        lock (_gate)
        {
            if (!reservation.TryCommit())
            {
                return;
            }

            _pending--;
            _retained++;
        }
    }

    internal void Rollback(RetainedAgentReservation reservation)
    {
        lock (_gate)
        {
            if (!reservation.TryRollback())
            {
                return;
            }

            _pending--;
        }
    }

    internal void Release(RetainedAgentReservation reservation)
    {
        lock (_gate)
        {
            if (!reservation.TryRelease())
            {
                return;
            }

            _retained--;
        }
    }
}
