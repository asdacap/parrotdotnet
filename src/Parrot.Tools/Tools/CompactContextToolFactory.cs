using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Tools;

internal sealed class CompactContextToolFactory(IPromptTemplateCatalog promptTemplates) : IToolFactory
{
    public ITool Create(IAgentSession session) => new CompactContextTool(session, promptTemplates);
}
