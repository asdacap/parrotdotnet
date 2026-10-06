namespace Parrot.Config;

internal sealed record AgentTaskConfig
{
    public AgentTaskConfig(int maximumAttempts, int maximumResponseRepairs, AgentTaskForkHistoryMode forkHistoryMode, IPromptTemplateCatalog promptTemplates)
    {
        MaximumAttempts = maximumAttempts;
        MaximumResponseRepairs = maximumResponseRepairs;
        ForkHistoryMode = forkHistoryMode;
        PromptTemplates = promptTemplates;
    }

    public int MaximumAttempts { get; }

    public int MaximumResponseRepairs { get; }

    public AgentTaskForkHistoryMode ForkHistoryMode { get; }

    public IPromptTemplateCatalog PromptTemplates { get; }
}
