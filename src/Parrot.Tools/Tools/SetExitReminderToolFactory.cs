using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Tools;

internal sealed class SetExitReminderToolFactory(IExitReminder reminder, IPromptTemplateCatalog promptTemplates) : IToolFactory
{
    public ITool Create(IAgentSession session) => new SetExitReminderTool(reminder, promptTemplates);
}
