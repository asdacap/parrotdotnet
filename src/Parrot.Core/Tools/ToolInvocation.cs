using Parrot.Config;

namespace Parrot.Tools;

internal sealed record ToolInvocation(string CallId, string ArgumentsJson, long AssistantSequence)
{
    public ToolInvocation(string callId, string argumentsJson)
        : this(callId, argumentsJson, 0)
    {
    }

    public PromptTemplateCatalog? PromptTemplates { get; init; }

    public ToolCycleImageBudget? ImageBudget { get; init; }
}
