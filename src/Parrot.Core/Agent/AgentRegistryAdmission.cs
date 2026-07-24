namespace Parrot.Agent;

internal sealed class AgentRegistryAdmission
{
    private const int MaxConcurrent = 8;

    private readonly Lock _gate = new();
    private int _active;

    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_active >= MaxConcurrent)
            {
                return false;
            }

            _active++;
            return true;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            _active--;
        }
    }
}
