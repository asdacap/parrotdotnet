using Parrot.Agent;

namespace Parrot.Context;

internal sealed class SessionIdentityProvider : ISystemPromptProvider
{
    public string Key => "runtime:system-context:08-session-identity";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new StaticSystemPrompt(identity.Context);
    }
}
