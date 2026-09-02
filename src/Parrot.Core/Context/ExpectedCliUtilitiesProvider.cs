using Parrot.Agent;
using Parrot.Config;
using Parrot.Process;

namespace Parrot.Context;

internal sealed class ExpectedCliUtilitiesProvider(CliUtilityAvailability cliUtilities, PromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:03-cli-utilities";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var utilities = cliUtilities.AvailableExpected.Count == 0
            ? "none"
            : string.Join(", ", cliUtilities.AvailableExpected);
        return new StaticSystemPrompt(templates.Render("context.expected-cli-utilities", [
            new PromptTemplateArgument("utilities", utilities),
        ]));
    }
}
