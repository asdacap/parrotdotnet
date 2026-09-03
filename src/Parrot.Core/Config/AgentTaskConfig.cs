namespace Parrot.Config;

internal sealed record AgentTaskConfig
{
    public AgentTaskConfig(int maximumAttempts, PromptTemplateCatalog promptTemplates)
    {
        MaximumAttempts = maximumAttempts;
        PromptTemplates = promptTemplates;
    }

    public int MaximumAttempts { get; }

    public PromptTemplateCatalog PromptTemplates { get; }
}
