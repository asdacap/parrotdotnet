using System.Runtime.InteropServices;
using Parrot.Agent;

namespace Parrot.Context;

internal sealed class PlatformProvider : ISystemPromptProvider
{
    public string Key => "runtime:system-context:05-platform";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new StaticSystemPrompt($"Platform: {RuntimeInformation.RuntimeIdentifier}");
    }
}
