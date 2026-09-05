namespace Parrot.AgentTasks;

internal interface IAgentTaskRunCompletion
{
    Task Deliver(AgentTaskRunTerminal terminal, CancellationToken cancellationToken);

    Task DeliverDuringShutdown(AgentTaskRunTerminal terminal, CancellationToken cancellationToken);
}
