using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Tools;

internal sealed class CompactContextToolFactory(PromptTemplateCatalog promptTemplates) : IToolFactory
{
    public ITool Create(IAgentSession session) => new CompactContextTool((IAgentSessionContext)session, promptTemplates);
}
