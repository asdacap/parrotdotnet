using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Tools;

internal sealed class SetExitReminderToolFactory(IExitReminder reminder, IPromptTemplateCatalog promptTemplates, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("set_exit_reminder");

    public ITool Create(IAgentSession session) => new SetExitReminderTool(reminder, promptTemplates);
}
