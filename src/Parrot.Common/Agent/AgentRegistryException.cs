namespace Parrot.Agent;

public sealed class AgentRegistryException : Exception
{
    public AgentRegistryException()
    {
    }

    public AgentRegistryException(string message)
        : base(message)
    {
    }

    public AgentRegistryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
