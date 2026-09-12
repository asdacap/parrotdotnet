using Parrot.Agent;
using Parrot.Config;
using Parrot.Process;

namespace Parrot.Context;

internal sealed class OptionalCliUtilitiesProvider(CliUtilityAvailability cliUtilities, IPromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:08-optional-cli-utilities";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var utilities = cliUtilities.AvailableOptional.Count == 0
            ? "none"
            : string.Join(", ", cliUtilities.AvailableOptional);
        return new StaticSystemPrompt(templates.Render("context.optional-cli-utilities", [
            new PromptTemplateArgument("utilities", utilities),
        ]));
    }
}
