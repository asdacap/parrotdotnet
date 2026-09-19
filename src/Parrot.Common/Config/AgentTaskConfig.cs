namespace Parrot.Config;

internal sealed record AgentTaskConfig
{
    public AgentTaskConfig(int maximumAttempts, int maximumResponseRepairs, bool forkParentHistory, IPromptTemplateCatalog promptTemplates)
    {
        MaximumAttempts = maximumAttempts;
        MaximumResponseRepairs = maximumResponseRepairs;
        ForkParentHistory = forkParentHistory;
        PromptTemplates = promptTemplates;
    }

    public int MaximumAttempts { get; }

    public int MaximumResponseRepairs { get; }

    public bool ForkParentHistory { get; }

    public IPromptTemplateCatalog PromptTemplates { get; }
}
