using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Context;

internal sealed class DateProvider(string date, PromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:04-date";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new StaticSystemPrompt(templates.Render("context.date", [
            new PromptTemplateArgument("date", date),
        ]));
    }
}
