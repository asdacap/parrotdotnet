namespace Parrot.AgentTasks;

internal sealed record AgentTaskRunTerminal(
    string RunId,
    string CompletionMessageId,
    AgentTaskExecutionStatus Status,
    string Result,
    string Error);
