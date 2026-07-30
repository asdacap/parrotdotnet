using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Context;

internal sealed class OptionalCliUtilitiesProvider(CliUtilityAvailability cliUtilities) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:07-optional-cli-utilities";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var utilities = cliUtilities.AvailableOptional.Count == 0
            ? "none"
            : string.Join(", ", cliUtilities.AvailableOptional);
        return new StaticSystemPrompt($"Available optional CLI utilities: {utilities}");
    }
}
