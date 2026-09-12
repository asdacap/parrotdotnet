using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Tools;

internal sealed class SetExitReminderToolFactory(ExitReminder reminder, IPromptTemplateCatalog promptTemplates) : IToolFactory
{
    public ITool Create(IAgentSession session) => new SetExitReminderTool(reminder, promptTemplates);
}
