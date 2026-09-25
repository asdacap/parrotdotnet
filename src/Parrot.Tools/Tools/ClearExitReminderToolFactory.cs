using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Tools;

internal sealed class ClearExitReminderToolFactory(IExitReminder reminder, IPromptTemplateCatalog promptTemplates, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("clear_exit_reminder");

    public ITool Create(IAgentSession session) => new ClearExitReminderTool(reminder, promptTemplates);
}
