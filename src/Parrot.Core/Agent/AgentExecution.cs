namespace Parrot.Agent;

internal sealed record AgentExecution(AgentExecutionStatus Status, string Output, string Error)
{
    public static AgentExecution Succeeded(string output) =>
        new(AgentExecutionStatus.Succeeded, output, string.Empty);

    public static AgentExecution Failed(string error) =>
        new(AgentExecutionStatus.Failed, string.Empty, error);

    public static AgentExecution Canceled() =>
        new(AgentExecutionStatus.Canceled, string.Empty, "interrupted");
}
