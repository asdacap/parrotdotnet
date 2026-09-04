namespace Parrot.Config;

internal sealed record AgentTaskConfig
{
    public AgentTaskConfig(int maximumAttempts, bool forkParentHistory, PromptTemplateCatalog promptTemplates)
    {
        MaximumAttempts = maximumAttempts;
        ForkParentHistory = forkParentHistory;
        PromptTemplates = promptTemplates;
    }

    public int MaximumAttempts { get; }

    public bool ForkParentHistory { get; }

    public PromptTemplateCatalog PromptTemplates { get; }
}
