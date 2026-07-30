using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Context;

internal sealed class SystemContextProvider(
    string basePrompt,
    string workingDirectory,
    string configDirectory,
    string date,
    ProfileRegistry profiles,
    CliUtilityAvailability cliUtilities) : ISystemPromptProvider
{
    public string Key => "runtime:system-context";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new SystemContextPrompt(
            basePrompt,
            workingDirectory,
            configDirectory,
            date,
            identity.Context,
            profiles.Children,
            cliUtilities);
    }
}
