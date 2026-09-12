namespace Parrot.Config;

internal sealed record AgentTaskConfig
{
    public AgentTaskConfig(int maximumAttempts, bool forkParentHistory, IPromptTemplateCatalog promptTemplates)
    {
        MaximumAttempts = maximumAttempts;
        ForkParentHistory = forkParentHistory;
        PromptTemplates = promptTemplates;
    }

    public int MaximumAttempts { get; }

    public bool ForkParentHistory { get; }

    public IPromptTemplateCatalog PromptTemplates { get; }
}
