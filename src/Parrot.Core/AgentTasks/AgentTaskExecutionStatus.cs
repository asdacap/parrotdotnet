namespace Parrot.AgentTasks;

internal enum AgentTaskExecutionStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Blocked,
    Canceled,
}
