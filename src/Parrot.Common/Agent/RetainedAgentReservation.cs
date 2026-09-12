namespace Parrot.Agent;

internal sealed class RetainedAgentReservation(RetainedAgentBudget owner)
{
    private ReservationState _state;

    private enum ReservationState
    {
        Pending,
        Retained,
        Released,
    }

    public void Commit() => owner.Commit(this);

    public void Rollback() => owner.Rollback(this);

    public void Release() => owner.Release(this);

    internal bool TryCommit()
    {
        if (_state != ReservationState.Pending)
        {
            return false;
        }

        _state = ReservationState.Retained;
        return true;
    }

    internal bool TryRollback()
    {
        if (_state != ReservationState.Pending)
        {
            return false;
        }

        _state = ReservationState.Released;
        return true;
    }

    internal bool TryRelease()
    {
        if (_state != ReservationState.Retained)
        {
            return false;
        }

        _state = ReservationState.Released;
        return true;
    }
}
