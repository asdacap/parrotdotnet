namespace Parrot.Agent;

public sealed class AgentExecutionException : Exception
{
    public AgentExecutionException()
    {
    }

    public AgentExecutionException(string message)
        : base(message)
    {
    }

    public AgentExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal AgentExecutionException(AgentTaskStatus status, string message)
        : base(message) => Status = status;

    internal AgentTaskStatus Status { get; }
}
