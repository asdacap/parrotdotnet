using Parrot.Config;

namespace Parrot.Tools;

internal sealed class AgentSendWithoutParentAmendment(IPromptTemplateCatalog templates) : IToolDefinitionAmendment
{
    public ToolDefinitionCatalog Amend(ToolDefinitionCatalog catalog) =>
        catalog.ReplaceDescription("agent_send", templates.Render("agent-send-tool.description-without-parent", []));
}
