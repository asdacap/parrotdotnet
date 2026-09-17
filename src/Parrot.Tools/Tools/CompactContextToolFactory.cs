using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Tools;

internal sealed class CompactContextToolFactory(IPromptTemplateCatalog promptTemplates, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("compact_context");

    public ITool Create(IAgentSession session) => new CompactContextTool(session, promptTemplates);
}
