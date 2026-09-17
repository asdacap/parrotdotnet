using Parrot.Config;

namespace Parrot.Tools;

internal sealed class AgentSendWithoutParentDefinition(
    IToolDefinition configured,
    IPromptTemplateCatalog templates) : IToolDefinition
{
    public string Description => templates.Render("agent-send-tool.description-without-parent", []);

    public string ParametersJson => configured.ParametersJson;
}
